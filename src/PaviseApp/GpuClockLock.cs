using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal static class GpuClockLock
	{
		private sealed class Device
		{
			public IntPtr Handle;

			public string Uuid;
		}

		internal const string ReceiptKey = "GpuClockLockReceipt";

		private const int NvmlSuccess = 0;

		private const int NvmlNotSupported = 3;

		private const string AmdKey = "GpuClockLockAmd";

		private static readonly object lk = new object();

		public static bool HasResidue
		{
			get
			{
				if (Settings.LoadStr("GpuClockLockReceipt", "").Length <= 0 && !Settings.Load("GpuClockLockAmd", false))
				{
					return AdlxTweaks.HasGfxMinResidue();
				}
				return true;
			}
		}

		[DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
		private static extern int NvmlInit();

		[DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
		private static extern int NvmlShutdown();

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
		private static extern int NvmlGetCount(out uint count);

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
		private static extern int NvmlGetHandle(uint index, out IntPtr device);

		[DllImport("nvml.dll", CharSet = CharSet.Ansi, EntryPoint = "nvmlDeviceGetUUID")]
		private static extern int NvmlGetUuid(IntPtr device, StringBuilder buffer, uint length);

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceResetGpuLockedClocks")]
		private static extern int NvmlResetLockedClocks(IntPtr device);

		private static bool Open(List<Device> devices)
		{
			try
			{
				if (NvmlInit() != 0)
				{
					return false;
				}
				uint count;
				if (NvmlGetCount(out count) != 0)
				{
					NvmlShutdown();
					return false;
				}
				for (uint num = 0u; num < count; num++)
				{
					IntPtr device;
					if (NvmlGetHandle(num, out device) == 0)
					{
						StringBuilder stringBuilder = new StringBuilder(96);
						if (NvmlGetUuid(device, stringBuilder, (uint)stringBuilder.Capacity) == 0)
						{
							devices.Add(new Device
							{
								Handle = device,
								Uuid = stringBuilder.ToString()
							});
						}
					}
				}
				return true;
			}
			catch (DllNotFoundException)
			{
				return false;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
			catch
			{
				return false;
			}
		}

		private static void Close()
		{
			try
			{
				NvmlShutdown();
			}
			catch
			{
			}
		}

		internal static string EncodeReceipt(IList<KeyValuePair<string, uint>> locked)
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, uint> item in locked)
			{
				list.Add(item.Key + "|" + item.Value.ToString(CultureInfo.InvariantCulture));
			}
			return string.Join(";", list.ToArray());
		}

		internal static List<KeyValuePair<string, uint>> DecodeReceipt(string raw)
		{
			List<KeyValuePair<string, uint>> list = new List<KeyValuePair<string, uint>>();
			if (string.IsNullOrEmpty(raw))
			{
				return list;
			}
			string[] array = raw.Split(';');
			foreach (string text in array)
			{
				int num = text.IndexOf('|');
				uint result;
				if (num > 0 && uint.TryParse(text.Substring(num + 1), NumberStyles.None, CultureInfo.InvariantCulture, out result))
				{
					list.Add(new KeyValuePair<string, uint>(text.Substring(0, num), result));
				}
			}
			return list;
		}

		public static bool Restore()
		{
			lock (lk)
			{
				if (Settings.Load("GpuClockLockAmd", false) || AdlxTweaks.HasGfxMinResidue())
				{
					if (!AdlxTweaks.RestoreGfxMin())
					{
						return false;
					}
					Settings.Save("GpuClockLockAmd", false);
				}
				string text = Settings.LoadStr("GpuClockLockReceipt", "");
				if (text.Length == 0)
				{
					return true;
				}
				List<KeyValuePair<string, uint>> list = DecodeReceipt(text);
				List<Device> list2 = new List<Device>();
				if (!Open(list2))
				{
					Logger.Log(Lang.T("log.gpuclock.6"));
					return false;
				}
				try
				{
					bool flag = true;
					foreach (KeyValuePair<string, uint> item in list)
					{
						Device device = null;
						foreach (Device item2 in list2)
						{
							if (string.Equals(item2.Uuid, item.Key, StringComparison.OrdinalIgnoreCase))
							{
								device = item2;
								break;
							}
						}
						if (device != null)
						{
							int num = NvmlResetLockedClocks(device.Handle);
							if (num != 0 && num != 3)
							{
								flag = false;
							}
						}
					}
					if (!flag)
					{
						Logger.Log(Lang.T("log.gpuclock.6"));
						return false;
					}
					Settings.SaveStr("GpuClockLockReceipt", "");
					Logger.Log(Lang.T("log.gpuclock.7"));
					return true;
				}
				finally
				{
					Close();
				}
			}
		}

		public static void HealFromCrash()
		{
			if (HasResidue && Restore())
			{
				Logger.Log(Lang.T("log.gpuclock.8"));
			}
		}
	}
}
