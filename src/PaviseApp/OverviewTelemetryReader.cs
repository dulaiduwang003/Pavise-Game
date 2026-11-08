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
			ulong kernelDeltaOffset = kernelDelta + userDelta;
			if (kernelDeltaOffset == 0 || idleDelta > kernelDeltaOffset)
			{
				return null;
			}
			return Math.Max(0.0, Math.Min(100.0, 100.0 * (1.0 - (double)idleDelta / (double)kernelDeltaOffset)));
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
			uint pdhGetFormattedCounterArrayWResult = PdhGetFormattedCounterArrayW(gpuCounter, 33280u, ref bytes, ref count, IntPtr.Zero);
			if (pdhGetFormattedCounterArrayWResult != 2147485650u || bytes == 0 || bytes > 4194304)
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
				pdhGetFormattedCounterArrayWResult = PdhGetFormattedCounterArrayW(gpuCounter, 33280u, ref bytes, ref count, gpuBuffer);
				if (pdhGetFormattedCounterArrayWResult == 2147485650u && bytes > gpuBufferCapacity && bytes <= 4194304)
				{
					continue;
				}
				if (pdhGetFormattedCounterArrayWResult != 0)
				{
					return null;
				}
				int sizeOfResult = Marshal.SizeOf(typeof(PdhValueItem));
				if (count == 0 || (ulong)(count * sizeOfResult) > (ulong)gpuBufferCapacity)
				{
					return null;
				}
				List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>((int)count);
				for (uint iterationIndex = 0u; iterationIndex < count; iterationIndex++)
				{
					PdhValueItem pdhValueItem = (PdhValueItem)Marshal.PtrToStructure(new IntPtr(gpuBuffer.ToInt64() + iterationIndex * sizeOfResult), typeof(PdhValueItem));
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
				string gpuEngineKeyResult = GpuEngineKey(value2.Key);
				if (gpuEngineKeyResult != null && !double.IsNaN(value2.Value) && !double.IsInfinity(value2.Value) && !(value2.Value < 0.0))
				{
					double value;
					dictionary.TryGetValue(gpuEngineKeyResult, out value);
					dictionary[gpuEngineKeyResult] = Math.Min(100.0, value + Math.Min(100.0, value2.Value));
				}
			}
			if (dictionary.Count == 0)
			{
				return null;
			}
			double aggregateGpu3DResult = 0.0;
			foreach (double value3 in dictionary.Values)
			{
				double val = value3;
				aggregateGpu3DResult = Math.Max(aggregateGpu3DResult, val);
			}
			return aggregateGpu3DResult;
		}

		internal static string GpuEngineKey(string instance)
		{
			if (string.IsNullOrEmpty(instance) || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			int matchIndex = instance.IndexOf('_', 4);
			if (matchIndex <= 4 || !Digits(instance, 4, matchIndex))
			{
				return null;
			}
			int matchIndex2 = instance.IndexOf("_engtype_3D", matchIndex, StringComparison.OrdinalIgnoreCase);
			if (matchIndex2 < 0)
			{
				return null;
			}
			int matchIndex2Offset = matchIndex2 + "_engtype_3D".Length;
			if (matchIndex2Offset < instance.Length && (instance[matchIndex2Offset] != '#' || !Digits(instance, matchIndex2Offset + 1, instance.Length)))
			{
				return null;
			}
			string substringResult = instance.Substring(matchIndex + 1, matchIndex2 - matchIndex - 1);
			if (!substringResult.StartsWith("luid_", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			int matchIndex3 = substringResult.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
			int matchIndex4 = substringResult.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
			if (matchIndex3 <= 5 || matchIndex4 <= matchIndex3 + 6 || !Digits(substringResult, matchIndex3 + 6, matchIndex4) || !Digits(substringResult, matchIndex4 + 5, substringResult.Length))
			{
				return null;
			}
			int matchIndex5 = substringResult.IndexOf('_', 5);
			if (matchIndex5 < 0 || !HexWord(substringResult, 5, matchIndex5) || !HexWord(substringResult, matchIndex5 + 1, matchIndex3))
			{
				return null;
			}
			return substringResult;
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
				int processedCount = 0;
				long numericValue;
				int physicalCoreCountValue;
				for (numericValue = 0L; numericValue + 8 <= bytes; numericValue += physicalCoreCountValue)
				{
					IntPtr ptr = new IntPtr(intPtr.ToInt64() + numericValue);
					physicalCoreCountValue = Marshal.ReadInt32(ptr, 4);
					if (physicalCoreCountValue < 8 || numericValue + physicalCoreCountValue > bytes)
					{
						return 0;
					}
					if (Marshal.ReadInt32(ptr) == 0)
					{
						processedCount++;
					}
				}
				return (numericValue == bytes) ? processedCount : 0;
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
						string message = null;
						int maxValue = int.MaxValue;
						int processedCount = 0;
						bool memoryFactsReady = true;
						bool memoryFactsReady2 = true;
						bool memoryFactsReady3 = true;
						ulong numericValue = 0uL;
						foreach (ManagementObject item in managementObjectCollection)
						{
							using (item)
							{
								ulong toUnsignedResult = ToUnsigned(item["Capacity"]);
								if (toUnsignedResult == 0 || toUnsignedResult > (ulong)(-1L - (long)numericValue))
								{
									memoryFactsReady3 = false;
								}
								else
								{
									numericValue += toUnsignedResult;
								}
								string memoryTypeNameResult = MemoryTypeName((int)ToUnsigned(item["SMBIOSMemoryType"]));
								if (memoryTypeNameResult == null || (message != null && message != memoryTypeNameResult))
								{
									memoryFactsReady = false;
								}
								else
								{
									message = memoryTypeNameResult;
								}
								ulong toUnsignedResult2 = ToUnsigned(item["ConfiguredClockSpeed"]);
								if (toUnsignedResult2 == 0 || toUnsignedResult2 > 100000)
								{
									memoryFactsReady2 = false;
								}
								else
								{
									maxValue = Math.Min(maxValue, (int)toUnsignedResult2);
								}
								processedCount++;
							}
						}
						if (processedCount > 0)
						{
							memoryFacts.Type = (memoryFactsReady ? message : null);
							memoryFacts.Speed = ((memoryFactsReady2 && maxValue != int.MaxValue) ? maxValue : 0);
							memoryFacts.Capacity = (memoryFactsReady3 ? numericValue : 0);
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
