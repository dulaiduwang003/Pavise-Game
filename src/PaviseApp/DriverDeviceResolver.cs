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
			int matchIndex = deviceId.IndexOf('\\');
			if (matchIndex < 0)
			{
				return deviceId;
			}
			string substringResult = deviceId.Substring(matchIndex + 1);
			int matchIndex2 = substringResult.IndexOf('&');
			string substringResult2 = ((matchIndex2 > 0) ? substringResult.Substring(0, matchIndex2) : substringResult);
			return deviceId.Substring(0, matchIndex) + "\\" + substringResult2;
		}
	}
}
