using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class DisplayGuard
	{
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct DEVMODE
		{
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
			public string dmDeviceName;

			public ushort dmSpecVersion;

			public ushort dmDriverVersion;

			public ushort dmSize;

			public ushort dmDriverExtra;

			public int dmFields;

			public int dmPositionX;

			public int dmPositionY;

			public int dmDisplayOrientation;

			public int dmDisplayFixedOutput;

			public short dmColor;

			public short dmDuplex;

			public short dmYResolution;

			public short dmTTOption;

			public short dmCollate;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
			public string dmFormName;

			public ushort dmLogPixels;

			public int dmBitsPerPel;

			public int dmPelsWidth;

			public int dmPelsHeight;

			public int dmDisplayFlags;

			public int dmDisplayFrequency;

			public int dmICMMethod;

			public int dmICMIntent;

			public int dmMediaType;

			public int dmDitherType;

			public int dmReserved1;

			public int dmReserved2;

			public int dmPanningWidth;

			public int dmPanningHeight;
		}

		private const string Slot = "PrevRefreshHz";

		private const int ENUM_CURRENT_SETTINGS = -1;

		private static readonly object lk = new object();

		internal static int CurrentRefreshRate()
		{
			try
			{
				string deviceName = Screen.PrimaryScreen.DeviceName;
				DEVMODE dm = NewDm();
				if (!EnumDisplaySettingsW(deviceName, -1, ref dm))
				{
					return 0;
				}
				return dm.dmDisplayFrequency;
			}
			catch
			{
				return 0;
			}
		}

		internal static int MaxRefreshRate()
		{
			int maxRefreshRateResult = 0;
			foreach (int item in AllRefreshRates())
			{
				if (item > maxRefreshRateResult)
				{
					maxRefreshRateResult = item;
				}
			}
			if (maxRefreshRateResult <= 0)
			{
				return CurrentRefreshRate();
			}
			return maxRefreshRateResult;
		}

		internal static List<int> AllRefreshRates()
		{
			List<int> list = new List<int>();
			try
			{
				Screen[] allScreens = Screen.AllScreens;
				foreach (Screen screen in allScreens)
				{
					DEVMODE dm = NewDm();
					if (EnumDisplaySettingsW(screen.DeviceName, -1, ref dm) && dm.dmDisplayFrequency > 1)
					{
						list.Add(dm.dmDisplayFrequency);
					}
				}
			}
			catch
			{
			}
			return list;
		}

		internal static void QueryRefreshRates(out int current, out int best)
		{
			current = 0;
			best = 0;
			try
			{
				string deviceName = Screen.PrimaryScreen.DeviceName;
				DEVMODE dm = NewDm();
				if (!EnumDisplaySettingsW(deviceName, -1, ref dm))
				{
					return;
				}
				current = dm.dmDisplayFrequency;
				best = current;
				DEVMODE dm2 = NewDm();
				for (int i = 0; EnumDisplaySettingsW(deviceName, i, ref dm2); i++)
				{
					if (dm2.dmPelsWidth == dm.dmPelsWidth && dm2.dmPelsHeight == dm.dmPelsHeight && dm2.dmBitsPerPel == dm.dmBitsPerPel && dm2.dmDisplayFrequency > best)
					{
						best = dm2.dmDisplayFrequency;
					}
					dm2 = NewDm();
				}
			}
			catch
			{
			}
		}

		public static bool HasResidue()
		{
			return Settings.LoadStr("PrevRefreshHz", "").Length > 0;
		}

		public static bool Restore()
		{
			lock (lk)
			{
				string prevRefreshHzValue = Settings.LoadStr("PrevRefreshHz", "");
				if (prevRefreshHzValue.Length == 0)
				{
					return true;
				}
				Settings.SaveStr("PrevRefreshHz", "");
				Logger.Log(Lang.T("log.displayguard.7"));
				return Settings.LoadStr("PrevRefreshHz", "").Length == 0;
			}
		}

		private static DEVMODE NewDm()
		{
			DEVMODE result = default(DEVMODE);
			result.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
			return result;
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
	}
}
