using System;

namespace PaviseApp
{
	internal static class DriverDeviceResolver
	{
		private static readonly string[] NeverTouch = new string[20]
		{
			"ntoskrnl.exe", "ntkrnlmp.exe", "hal.dll", "ndis.sys", "tcpip.sys", "dxgkrnl.sys", "dxgmms1.sys", "dxgmms2.sys", "win32k.sys", "win32kbase.sys",
			"storport.sys", "storsvc.sys", "usbport.sys", "acpi.sys", "pci.sys", "wdf01000.sys", "clfs.sys", "fltmgr.sys", "ntfs.sys", "volmgr.sys"
		};

		internal static bool IsFramework(string driver)
		{
			string[] neverTouch = NeverTouch;
			foreach (string a in neverTouch)
			{
				if (string.Equals(a, driver, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		internal static string ShortId(string deviceId)
		{
			if (string.IsNullOrEmpty(deviceId))
			{
				return "";
			}
			int num = deviceId.IndexOf('\\');
			if (num < 0)
			{
				return deviceId;
			}
			string text = deviceId.Substring(num + 1);
			int num2 = text.IndexOf('&');
			string text2 = ((num2 > 0) ? text.Substring(0, num2) : text);
			return deviceId.Substring(0, num) + "\\" + text2;
		}
	}
}
