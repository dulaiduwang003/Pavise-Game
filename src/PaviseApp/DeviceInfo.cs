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
			string text = CpuName();
			string text2 = GpuFromRegistry();
			if (string.IsNullOrEmpty(text2) && allowSlow)
			{
				text2 = GpuFromWmi();
			}
			string text3 = MemoryText();
			string[] array = new string[3]
			{
				string.IsNullOrEmpty(text) ? " " : text,
				string.IsNullOrEmpty(text2) ? " " : text2,
				string.IsNullOrEmpty(text3) ? " " : text3
			};
			if (array[1] != " ")
			{
				cached = array;
			}
			return array;
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
					string text = registryKey.GetValue("ProcessorNameString") as string;
					return (text == null) ? null : Compact(text);
				}
			}
			catch
			{
				return null;
			}
		}

		private static string GpuFromRegistry()
		{
			GpuAdapter gpuAdapter = GpuInventory.Primary();
			if (gpuAdapter != null)
			{
				return gpuAdapter.Name;
			}
			return null;
		}

		private static string GpuFromWmi()
		{
			string text = null;
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
								string text2 = Compact(item["Name"] as string);
								if (text2.Length != 0)
								{
									if (GpuInventory.VendorOf(hardwareId) == GpuVendor.Nvidia)
									{
										return text2;
									}
									if (text == null)
									{
										text = text2;
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
			return text;
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
			string text = (value ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(C)", "")
				.Replace("®", "")
				.Replace("™", "");
			text = text.Replace(" CPU", "").Replace(" Processor", "");
			while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
			{
				text = text.Replace("  ", " ");
			}
			return text.Trim();
		}
	}
}
