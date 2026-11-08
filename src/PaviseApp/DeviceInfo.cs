using System;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class DeviceInfo
	{
		private struct MemoryStatusEx
		{
			public uint Length;

			public uint MemoryLoad;

			public ulong TotalPhys;

			public ulong AvailPhys;

			public ulong TotalPageFile;

			public ulong AvailPageFile;

			public ulong TotalVirtual;

			public ulong AvailVirtual;

			public ulong AvailExtendedVirtual;
		}

		private static string[] cached;

		public static string[] Specs()
		{
			return Build(false);
		}

		public static string[] SpecsWithSlowFallback()
		{
			return Build(true);
		}

		private static string[] Build(bool allowSlow)
		{
			if (cached != null)
			{
				return cached;
			}
			string cpuNameResult = CpuName();
			string gpuForDisplayResult = GpuForDisplay();
			if (string.IsNullOrEmpty(gpuForDisplayResult) && allowSlow)
			{
				gpuForDisplayResult = GpuFromWmi();
			}
			string memoryTextResult = MemoryText();
			string[] textEntries = new string[3]
			{
				string.IsNullOrEmpty(cpuNameResult) ? " " : cpuNameResult,
				string.IsNullOrEmpty(gpuForDisplayResult) ? " " : gpuForDisplayResult,
				string.IsNullOrEmpty(memoryTextResult) ? " " : memoryTextResult
			};
			if (textEntries[1] != " ")
			{
				cached = textEntries;
			}
			return textEntries;
		}

		private static string CpuName()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
				{
					if (registryKey == null)
					{
						return null;
					}
					string getValueOffset = registryKey.GetValue("ProcessorNameString") as string;
					return (getValueOffset == null) ? null : Compact(getValueOffset);
				}
			}
			catch
			{
				return null;
			}
		}

		private static string GpuForDisplay()
		{
			OverviewGpuInfo gpu = OverviewGpuReader.Read();
			return gpu == null ? null : gpu.Name;
		}

		private static string GpuFromWmi()
		{
			string gpuFromWmiResult = null;
			try
			{
				using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
				{
					using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
					{
						foreach (ManagementObject item in managementObjectCollection)
						{
							using (item)
							{
								string hardwareId = item["PNPDeviceID"] as string;
								if (!GpuInventory.IsPciAdapter(hardwareId))
								{
									continue;
								}
								string compactResult = Compact(item["Name"] as string);
								if (compactResult.Length != 0)
								{
									if (GpuInventory.VendorOf(hardwareId) == GpuVendor.Nvidia)
									{
										return compactResult;
									}
									if (gpuFromWmiResult == null)
									{
										gpuFromWmiResult = compactResult;
									}
								}
							}
						}
					}
				}
			}
			catch
			{
			}
			return gpuFromWmiResult;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

		private static string MemoryText()
		{
			try
			{
				MemoryStatusEx buffer = default(MemoryStatusEx);
				buffer.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
				if (!GlobalMemoryStatusEx(ref buffer) || buffer.TotalPhys == 0)
				{
					return null;
				}
				double a = (double)buffer.TotalPhys / 1073741824.0;
				return Math.Round(a).ToString("0") + " GB";
			}
			catch
			{
				return null;
			}
		}

		private static string Compact(string value)
		{
			string replaceResult = (value ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(C)", "")
				.Replace("®", "")
				.Replace("™", "");
			replaceResult = replaceResult.Replace(" CPU", "").Replace(" Processor", "");
			while (replaceResult.IndexOf("  ", StringComparison.Ordinal) >= 0)
			{
				replaceResult = replaceResult.Replace("  ", " ");
			}
			return replaceResult.Trim();
		}
	}
}
