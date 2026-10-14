// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal static class DriverDeviceResolver
    {
        private static readonly string[] NeverTouch =
        {
            "ntoskrnl.exe", "ntkrnlmp.exe", "hal.dll", "ndis.sys", "tcpip.sys",
            "dxgkrnl.sys", "dxgmms1.sys", "dxgmms2.sys", "win32k.sys", "win32kbase.sys",
            "storport.sys", "storsvc.sys", "usbport.sys", "acpi.sys", "pci.sys",
            "wdf01000.sys", "clfs.sys", "fltmgr.sys", "ntfs.sys", "volmgr.sys",
        };

        internal static bool IsFramework(string driver)
        {
            foreach (string value in NeverTouch)
                if (string.Equals(value,driver,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string ShortId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            int slash = deviceId.IndexOf('\\');
            if (slash < 0) return deviceId;
            string rest = deviceId.Substring(slash + 1);
            int amp = rest.IndexOf('&');
            string head = amp > 0 ? rest.Substring(0, amp) : rest;
            return deviceId.Substring(0, slash) + "\\" + head;
        }
    }
}
