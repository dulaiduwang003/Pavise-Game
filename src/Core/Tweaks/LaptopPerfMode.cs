// @author bdth 2074055628@qq.com
// File purpose Vendor performance mode is withdrawn; only cleanup of receipts left by older versions remains, switching back to the original mode by receipt at startup and on wipe
using System;
using System.Management;

namespace PaviseApp
{
    // Laptop PL1 and PL2 aren't in the Windows power scheme; vendor firmware assigns them per modes like quiet, balanced, performance
    //   Lenovo Gamezone interface SetSmartFanMode 1 quiet 2 balanced 3 performance; ASUS ATK interface device 0x120075 0 balanced 1 performance 2 quiet
    //   Switch defaults to on but always respects the user setting; not forced by tier
    //   Vendor tools change this value too; if the mode read at match end isn't what we wrote, don't take it back, just clear the record
    internal static class LaptopPerfMode
    {
        internal const string SnapKey = "LaptopPerfSnap";
        private const string Scope = @"root\WMI";
        private static readonly object lk = new object();

        private const int LenovoPerformance = 3;
        private const int AsusThrottleDevice = 0x00120075;
        private const int AsusPerformance = 1;

        public static bool HasResidue { get { return Settings.LoadStr(SnapKey, "").Length > 0; } }


        private enum Vendor { None, Lenovo, Asus }

        private static int vendorCache;

        private static Vendor Detect()
        {
            int cached = vendorCache;
            if (cached != 0) return (Vendor)(cached - 1);
            Vendor found = Vendor.None;
            try
            {
                if (!Native.HasSystemBattery()) found = Vendor.None;
                else if (ClassExists("LENOVO_GAMEZONE_DATA")) found = Vendor.Lenovo;
                else if (ClassExists("AsusAtkWmi_WMNB")) found = Vendor.Asus;
            }
            catch { found = Vendor.None; }
            vendorCache = (int)found + 1;
            return found;
        }

        private static bool ClassExists(string className)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM " + className))
                using (ManagementObjectCollection rows = searcher.Get())
                    foreach (ManagementObject row in rows) { row.Dispose(); return true; }
            }
            catch { }
            return false;
        }


        private static ManagementObject First(string className)
        {
            using (var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM " + className))
            using (ManagementObjectCollection rows = searcher.Get())
                foreach (ManagementObject row in rows) return row;
            return null;
        }

        private static bool ReadMode(Vendor vendor, out int mode)
        {
            mode = -1;
            try
            {
                if (vendor == Vendor.Lenovo)
                {
                    using (ManagementObject gz = First("LENOVO_GAMEZONE_DATA"))
                    {
                        if (gz == null) return false;
                        using (ManagementBaseObject result = gz.InvokeMethod("GetSmartFanMode", null, null))
                        {
                            if (result == null) return false;
                            mode = Convert.ToInt32(result["Data"]);
                            return mode >= 0;
                        }
                    }
                }
                if (vendor == Vendor.Asus)
                {
                    using (ManagementObject atk = First("AsusAtkWmi_WMNB"))
                    {
                        if (atk == null) return false;
                        using (ManagementBaseObject args = atk.GetMethodParameters("DSTS"))
                        {
                            args["Device_ID"] = (uint)AsusThrottleDevice;
                            using (ManagementBaseObject result = atk.InvokeMethod("DSTS", args, null))
                            {
                                if (result == null) return false;
                                uint status = Convert.ToUInt32(result["device_status"]);
                                // High bits are the support flag; only the low eight bits are the mode; high bits are 0 when unsupported
                                if ((status & 0x00010000u) == 0) return false;
                                mode = (int)(status & 0xFF);
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool WriteMode(Vendor vendor, int mode)
        {
            try
            {
                if (vendor == Vendor.Lenovo)
                {
                    using (ManagementObject gz = First("LENOVO_GAMEZONE_DATA"))
                    {
                        if (gz == null) return false;
                        using (ManagementBaseObject args = gz.GetMethodParameters("SetSmartFanMode"))
                        {
                            args["Data"] = (uint)mode;
                            using (gz.InvokeMethod("SetSmartFanMode", args, null)) { }
                        }
                    }
                    int check;
                    return ReadMode(vendor, out check) && check == mode;
                }
                if (vendor == Vendor.Asus)
                {
                    using (ManagementObject atk = First("AsusAtkWmi_WMNB"))
                    {
                        if (atk == null) return false;
                        using (ManagementBaseObject args = atk.GetMethodParameters("DEVS"))
                        {
                            args["Device_ID"] = (uint)AsusThrottleDevice;
                            args["Control_status"] = (uint)mode;
                            using (atk.InvokeMethod("DEVS", args, null)) { }
                        }
                    }
                    int check;
                    return ReadMode(vendor, out check) && check == mode;
                }
            }
            catch { }
            return false;
        }

        private static int PerformanceModeOf(Vendor vendor)
        {
            return vendor == Vendor.Lenovo ? LenovoPerformance : AsusPerformance;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string raw = Settings.LoadStr(SnapKey, "");
                if (raw.Length == 0) return true;
                string[] parts = raw.Split('|');
                int vendorRaw, original;
                if (parts.Length != 2 || !int.TryParse(parts[0], out vendorRaw) || !int.TryParse(parts[1], out original))
                { Settings.SaveStr(SnapKey, ""); return true; }
                var vendor = (Vendor)vendorRaw;
                int now;
                if (ReadMode(vendor, out now) && now != PerformanceModeOf(vendor))
                {
                    // Vendor tool or user changed it mid-match; don't take it back, just clear the record
                    Settings.SaveStr(SnapKey, "");
                    Logger.Log(Lang.T("log.laptopperf.5"));
                    return true;
                }
                if (!WriteMode(vendor, original)) { Logger.Log(Lang.T("log.laptopperf.4")); return false; }
                Settings.SaveStr(SnapKey, "");
                Logger.Log(Lang.T("log.laptopperf.6"));
                return true;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue && Restore()) Logger.Log(Lang.T("log.laptopperf.7"));
        }
    }
}
