using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal static class EnergyMeter
	{
		private struct DeviceInterfaceData
		{
			public int Size;

			public Guid InterfaceClassGuid;

			public int Flags;

			public IntPtr Reserved;
		}

		public sealed class Sample
		{
			internal readonly byte[] Raw;

			public int Channels
			{
				get
				{
					if (Raw != null)
					{
						return Raw.Length / 16;
					}
					return 0;
				}
			}

			internal Sample(byte[] raw)
			{
				Raw = raw;
			}
		}

		private const uint DigcfPresent = 2u;

		private const uint DigcfDeviceInterface = 16u;

		private const uint GenericRead = 2147483648u;

		private const uint ShareReadWrite = 3u;

		private const uint OpenExisting = 3u;

		private const uint IoctlVersion = 2244608u;

		private const uint IoctlMetadataSize = 2244612u;

		private const uint IoctlMetadata = 2244616u;

		private const uint IoctlMeasurement = 2244620u;

		internal const int ChannelStride = 16;

		internal const double PicoWattHoursPerHundredNsToWatts = 0.036;

		private static readonly Guid DeviceEnergyMeter = new Guid("45BD8344-7ED6-49CF-A440-C276C933B053");

		private static readonly object sync = new object();

		private static bool probed;

		private static string devicePath;

		private static bool unreadable;

		private static string[] channelNames = new string[0];

		private static string[] rawChannelNames = new string[0];

		private static int[] railIndex = new int[4] { -1, -1, -1, -1 };

		public static bool Available
		{
			get
			{
				lock (sync)
				{
					EnsureProbedLocked();
					return devicePath != null && !unreadable && railIndex[0] >= 0;
				}
			}
		}

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr window, uint flags);

		[DllImport("setupapi.dll", SetLastError = true)]
		private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref DeviceInterfaceData data);

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, int detailSize, out int required, IntPtr devInfoData);

		[DllImport("setupapi.dll")]
		private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);

		public static string Describe()
		{
			lock (sync)
			{
				EnsureProbedLocked();
				return DescribeLocked();
			}
		}

		internal static bool HasRail(EnergyRail rail)
		{
			lock (sync)
			{
				EnsureProbedLocked();
				return railIndex[(int)rail] >= 0;
			}
		}

		private static bool Has(string n, string token)
		{
			return n.IndexOf(token, StringComparison.Ordinal) >= 0;
		}

		internal static bool IsPerCore(string upper)
		{
			for (int num = upper.IndexOf("CORE", StringComparison.Ordinal); num >= 0; num = upper.IndexOf("CORE", num + 1, StringComparison.Ordinal))
			{
				int num2 = num + 4;
				if (num2 < upper.Length && upper[num2] >= '0' && upper[num2] <= '9')
				{
					return true;
				}
			}
			return false;
		}

		internal static int ClassifyRail(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return -1;
			}
			string text = name.ToUpperInvariant();
			if (Has(text, "DRAM") || Has(text, "MEMORY"))
			{
				return 1;
			}
			if (IsPerCore(text))
			{
				return -1;
			}
			if (Has(text, "PP0") || Has(text, "CORE"))
			{
				return 2;
			}
			if (Has(text, "PP1") || Has(text, "_GT") || Has(text, "IGPU"))
			{
				return 3;
			}
			if (Has(text, "PKG") || Has(text, "PACKAGE") || text == "CPU" || text.StartsWith("CPU_") || Has(text, "SOC"))
			{
				return 0;
			}
			return -1;
		}

		internal static double WattsFrom(ulong energyBefore, ulong energyAfter, ulong timeBefore, ulong timeAfter)
		{
			if (energyAfter <= energyBefore || timeAfter <= timeBefore)
			{
				return -1.0;
			}
			return 0.036 * (double)(energyAfter - energyBefore) / (double)(timeAfter - timeBefore);
		}

		public static Sample Take()
		{
			lock (sync)
			{
				EnsureProbedLocked();
				if (devicePath == null || unreadable)
				{
					return null;
				}
				IntPtr intPtr = CreateFileW(devicePath, 2147483648u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
				if (intPtr == new IntPtr(-1))
				{
					return null;
				}
				try
				{
					byte[] array = Ioctl(intPtr, 2244620u, 4096);
					return (array == null || array.Length < 16) ? null : new Sample(array);
				}
				finally
				{
					CloseHandle(intPtr);
				}
			}
		}

		public static double Watts(Sample before, Sample after, EnergyRail rail)
		{
			if (before == null || after == null)
			{
				return -1.0;
			}
			int num;
			lock (sync)
			{
				EnsureProbedLocked();
				num = railIndex[(int)rail];
			}
			if (num < 0)
			{
				return -1.0;
			}
			int num2 = num * 16;
			if (num2 + 16 > before.Raw.Length || num2 + 16 > after.Raw.Length)
			{
				return -1.0;
			}
			return WattsFrom(BitConverter.ToUInt64(before.Raw, num2), BitConverter.ToUInt64(after.Raw, num2), BitConverter.ToUInt64(before.Raw, num2 + 8), BitConverter.ToUInt64(after.Raw, num2 + 8));
		}

		private static void EnsureProbedLocked()
		{
			if (probed)
			{
				return;
			}
			probed = true;
			try
			{
				ProbeLocked();
			}
			catch
			{
				unreadable = true;
				channelNames = new string[0];
			}
			try
			{
				Logger.Log(Lang.T("log.energymeter.1") + DescribeLocked());
			}
			catch
			{
			}
		}

		private static string DescribeLocked()
		{
			if (devicePath == null)
			{
				return Lang.T("t.energymeter.1");
			}
			if (unreadable)
			{
				return Lang.T("t.energymeter.3");
			}
			if (channelNames.Length == 0)
			{
				return Lang.T("t.energymeter.2") + ((rawChannelNames.Length > 0) ? (" [" + string.Join(", ", rawChannelNames) + "]") : "");
			}
			return string.Join(", ", channelNames);
		}

		private static void ProbeLocked()
		{
			Guid guid = DeviceEnergyMeter;
			IntPtr intPtr = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 18u);
			if (intPtr == IntPtr.Zero || intPtr == new IntPtr(-1))
			{
				return;
			}
			List<string> list = new List<string>();
			try
			{
				for (uint num = 0u; num < 256; num++)
				{
					DeviceInterfaceData data = default(DeviceInterfaceData);
					data.Size = Marshal.SizeOf(typeof(DeviceInterfaceData));
					if (!SetupDiEnumDeviceInterfaces(intPtr, IntPtr.Zero, ref guid, num, ref data))
					{
						break;
					}
					int required;
					SetupDiGetDeviceInterfaceDetailW(intPtr, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
					if (required <= 0)
					{
						continue;
					}
					IntPtr intPtr2 = Marshal.AllocHGlobal(required);
					string text = null;
					try
					{
						Marshal.WriteInt32(intPtr2, (IntPtr.Size == 8) ? 8 : 6);
						int required2;
						if (SetupDiGetDeviceInterfaceDetailW(intPtr, ref data, intPtr2, required, out required2, IntPtr.Zero))
						{
							text = Marshal.PtrToStringUni(new IntPtr(intPtr2.ToInt64() + 4));
						}
					}
					finally
					{
						Marshal.FreeHGlobal(intPtr2);
					}
					if (text == null)
					{
						continue;
					}
					devicePath = text;
					unreadable = false;
					ReadChannelsLocked();
					string[] array = rawChannelNames;
					foreach (string item in array)
					{
						if (!list.Contains(item))
						{
							list.Add(item);
						}
					}
					if (!unreadable && railIndex[0] >= 0)
					{
						return;
					}
				}
			}
			finally
			{
				SetupDiDestroyDeviceInfoList(intPtr);
			}
			devicePath = null;
			channelNames = new string[0];
			rawChannelNames = list.ToArray();
			for (int j = 0; j < railIndex.Length; j++)
			{
				railIndex[j] = -1;
			}
		}

		private static void ReadChannelsLocked()
		{
			IntPtr intPtr = CreateFileW(devicePath, 2147483648u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (intPtr == new IntPtr(-1))
			{
				unreadable = true;
				return;
			}
			try
			{
				byte[] array = Ioctl(intPtr, 2244608u, 2);
				if (array == null || array.Length < 2)
				{
					unreadable = true;
					return;
				}
				byte[] array2 = Ioctl(intPtr, 2244612u, 8);
				if (array2 == null || array2.Length < 4)
				{
					unreadable = true;
					return;
				}
				long num = ((array2.Length >= 8) ? BitConverter.ToInt64(array2, 0) : BitConverter.ToUInt32(array2, 0));
				if (num <= 0 || num > 65536)
				{
					unreadable = true;
					return;
				}
				byte[] array3 = Ioctl(intPtr, 2244616u, (int)num);
				if (array3 == null)
				{
					unreadable = true;
					return;
				}
				List<string> list = new List<string>();
				List<string> list2 = new List<string>();
				foreach (string item in Utf16Strings(array3, 3))
				{
					list.Add(item);
					if (ClassifyRail(item) >= 0)
					{
						list2.Add(item);
					}
				}
				rawChannelNames = list.ToArray();
				channelNames = list2.ToArray();
				for (int i = 0; i < railIndex.Length; i++)
				{
					railIndex[i] = -1;
				}
				for (int j = 0; j < channelNames.Length; j++)
				{
					int num2 = ClassifyRail(channelNames[j]);
					if (num2 >= 0 && railIndex[num2] < 0)
					{
						railIndex[num2] = j;
					}
				}
			}
			finally
			{
				CloseHandle(intPtr);
			}
		}

		private static byte[] Ioctl(IntPtr handle, uint code, int outSize)
		{
			if (outSize <= 0 || outSize > 65536)
			{
				return null;
			}
			IntPtr intPtr = Marshal.AllocHGlobal(outSize);
			try
			{
				for (int i = 0; i < outSize; i++)
				{
					Marshal.WriteByte(intPtr, i, 0);
				}
				int returned;
				if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, intPtr, outSize, out returned, IntPtr.Zero))
				{
					return null;
				}
				if (returned <= 0 || returned > outSize)
				{
					returned = outSize;
				}
				byte[] array = new byte[returned];
				Marshal.Copy(intPtr, array, 0, returned);
				return array;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		internal static List<string> Utf16Strings(byte[] blob, int minLength)
		{
			List<string> list = new List<string>();
			if (blob == null)
			{
				return list;
			}
			StringBuilder stringBuilder = new StringBuilder();
			for (int i = 0; i + 1 < blob.Length; i += 2)
			{
				char c = (char)(blob[i] | (blob[i + 1] << 8));
				if (c >= ' ' && c < '\u007f')
				{
					stringBuilder.Append(c);
					continue;
				}
				if (stringBuilder.Length >= minLength)
				{
					list.Add(stringBuilder.ToString());
				}
				stringBuilder.Length = 0;
			}
			if (stringBuilder.Length >= minLength)
			{
				list.Add(stringBuilder.ToString());
			}
			return list;
		}
	}
}
