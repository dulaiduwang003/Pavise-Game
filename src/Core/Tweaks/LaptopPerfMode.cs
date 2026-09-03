// @author bdth 2074055628@qq.com
// 文件用途 对局中通过厂商 WMI 把笔记本切到性能档 退局切回原档 先支持 Lenovo Legion 和 ASUS ROG
using System;
using System.Management;

namespace PaviseApp
{
    // 笔记本的 PL1 PL2 不在 Windows 电源方案里 它们由厂商固件按"安静 均衡 性能"这类档位分配
    //   Lenovo Gamezone 接口 SetSmartFanMode 1 安静 2 均衡 3 性能  ASUS ATK 接口设备 0x120075 0 均衡 1 性能 2 安静
    //   开关默认开启但始终尊重用户配置 不由电竞或极限档强制
    //   厂商工具自己也会改这个值 退局时读到的档位不是我们写的就不抢回来 只清账
    internal static class LaptopPerfMode
    {
        internal const string SnapKey = "LaptopPerfSnap";
        private const string Scope = @"root\WMI";
        private static readonly object lk = new object();

        private const int LenovoPerformance = 3;
        private const int AsusThrottleDevice = 0x00120075;
        private const int AsusPerformance = 1;

        public static bool HasResidue { get { return Settings.LoadStr(SnapKey, "").Length > 0; } }

        internal static bool ShouldActivate(bool configured, bool supported)
        {
            return configured && supported;
        }

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

        public static bool SupportedCached() { return Detect() != Vendor.None; }

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
                                // 高位是支持标志 低八位才是档位 未支持时高位为 0
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

        public static bool Activate()
        {
            lock (lk)
            {
                if (HasResidue) return true;
                Vendor vendor = Detect();
                if (vendor == Vendor.None) { Logger.Log(Lang.T("log.laptopperf.1")); return false; }
                int current;
                if (!ReadMode(vendor, out current)) { Logger.Log(Lang.T("log.laptopperf.1")); return false; }
                int target = PerformanceModeOf(vendor);
                if (current == target) return true;
                string snap = (int)vendor + "|" + current;
                Settings.SaveStr(SnapKey, snap);
                if (Settings.LoadStr(SnapKey, "") != snap) return false;
                if (!WriteMode(vendor, target))
                {
                    Settings.SaveStr(SnapKey, "");
                    Logger.Warn(Lang.T("log.laptopperf.2"));
                    return false;
                }
                Logger.Log(Lang.T("log.laptopperf.3"));
                return true;
            }
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
                    // 厂商工具或用户中途改过 不抢回来 只清账
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
