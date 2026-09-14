// @author bdth 2074055628@qq.com
// File purpose Lands the driver image names identified by interrupt attribution on device instances that can actually be acted on
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class DriverDeviceMatch
    {
        public string Driver = "";
        public string Service = "";
        public readonly List<string> DeviceIds = new List<string>();
        public string Why = "";
        public bool Actionable { get { return DeviceIds.Count > 0; } }
    }

    internal static class DriverDeviceResolver
    {
        private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

        private static readonly string[] NeverTouch =
        {
            "ntoskrnl.exe", "ntkrnlmp.exe", "hal.dll", "ndis.sys", "tcpip.sys",
            "dxgkrnl.sys", "dxgmms1.sys", "dxgmms2.sys", "win32k.sys", "win32kbase.sys",
            "storport.sys", "storsvc.sys", "usbport.sys", "acpi.sys", "pci.sys",
            "wdf01000.sys", "clfs.sys", "fltmgr.sys", "ntfs.sys", "volmgr.sys",
        };

        private static readonly string[] AllowedBusPrefixes = { "PCI\\", "USB\\", "HDAUDIO\\", "ACPI\\" };

        internal static bool IsFramework(string driver)
        {
            foreach (string value in NeverTouch)
                if (string.Equals(value,driver,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static DriverDeviceMatch Resolve(string driverImageName)
        {
            var m = new DriverDeviceMatch();
            m.Driver = driverImageName ?? "";
            if (m.Driver.Length == 0) { m.Why = Lang.T("resolver.nodriver"); return m; }

            foreach (string bad in NeverTouch)
                if (string.Equals(m.Driver, bad, StringComparison.OrdinalIgnoreCase))
                { m.Why = Lang.T("resolver.kernel"); return m; }

            int dot = m.Driver.LastIndexOf('.');
            m.Service = dot > 0 ? m.Driver.Substring(0, dot) : m.Driver;
            if (m.Service.Length == 0) { m.Why = Lang.T("resolver.nosvc"); return m; }

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ServicesKey + m.Service + @"\Enum"))
                {
                    if (k == null) { m.Why = Lang.T("resolver.noenum"); return m; }
                    object cnt = k.GetValue("Count");
                    int count = 0;
                    if (cnt != null) try { count = Convert.ToInt32(cnt); } catch { count = 0; }
                    if (count <= 0) { m.Why = Lang.T("resolver.emptylist"); return m; }
                    if (count > 32) { m.Why = Lang.F("resolver.toomany", count); return m; }
                    for (int i = 0; i < count; i++)
                    {
                        string id = k.GetValue(i.ToString()) as string;
                        if (string.IsNullOrEmpty(id)) continue;
                        if (!BusAllowed(id)) continue;
                        if (!m.DeviceIds.Contains(id)) m.DeviceIds.Add(id);
                    }
                }
            }
            catch (Exception ex) { m.Why = Lang.F("resolver.readfail", ex.GetType().Name); return m; }

            if (m.DeviceIds.Count == 0) m.Why = Lang.T("resolver.nobus");
            return m;
        }

        private static bool BusAllowed(string deviceId)
        {
            foreach (string p in AllowedBusPrefixes)
                if (deviceId.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
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
