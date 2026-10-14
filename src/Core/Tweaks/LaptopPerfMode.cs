// 作者：bdth 2074055628@qq.com
using System;
using System.Management;

namespace PaviseApp
{
    internal static class LaptopPerfMode
    {
        internal const string SnapKey = "LaptopPerfSnap";
        internal const string PreferenceKey = "AutoLaptopPerformanceV1";
        private const string Scope = @"root\WMI";
        private static readonly object lk = new object();

        private const int LenovoPerformance = 3;
        private const int AsusThrottleDevice = 0x00120075;
        private const int AsusPerformance = 1;

        public static bool HasResidue
        {
            get
            {
                string receipt;
                return !Settings.TryLoadStr(SnapKey, out receipt) || !string.IsNullOrEmpty(receipt);
            }
        }

private enum Vendor { None, Lenovo, Asus }

        private static int vendorCache;

        internal interface IBackend
        {
            int DetectVendor();
            bool OnAcPower { get; }
            bool Read(int vendor, out int mode);
            bool Write(int vendor, int mode);
        }

        private sealed class FirmwareBackend : IBackend
        {
            public int DetectVendor() { return (int)Detect(); }
            public bool OnAcPower { get { return Native.ConfirmedAcPower(); } }
            public bool Read(int vendor, out int mode) { return ReadMode((Vendor)vendor, out mode); }
            public bool Write(int vendor, int mode) { return WriteMode((Vendor)vendor, mode); }
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static IBackend BackendForTest;
        internal static Func<string, bool> SaveSnapshotForTest;
        private sealed class UnavailableBackend : IBackend
        {
            public int DetectVendor() { return 0; }
            public bool OnAcPower { get { return false; } }
            public bool Read(int vendor, out int mode) { mode = -1; return false; }
            public bool Write(int vendor, int mode) { return false; }
        }
        private static readonly IBackend firmware = new UnavailableBackend();
#else
        private static readonly IBackend firmware = new FirmwareBackend();
#endif
        private static IBackend Backend
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (BackendForTest != null) return BackendForTest;
#endif
                return firmware;
            }
        }

        private static bool SaveSnapshot(string value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (SaveSnapshotForTest != null) return SaveSnapshotForTest(value);
#endif
            return Settings.SaveStrDurable(SnapKey, value);
        }

        private static bool KnownMode(int vendor, int mode)
        {
            return vendor == (int)Vendor.Lenovo ? mode >= 1 && mode <= 3
                : vendor == (int)Vendor.Asus && mode >= 0 && mode <= 2;
        }

        public static bool CanActivate
        {
            get
            {
                IBackend device = Backend;
                int vendor = device.OnAcPower ? device.DetectVendor() : 0;
                return vendor == (int)Vendor.Lenovo || vendor == (int)Vendor.Asus;
            }
        }

        public static bool Activate(Func<bool> admission)
        {
            lock (lk)
            {
                IBackend device = Backend;
                if (admission == null || !admission() || !device.OnAcPower || HasResidue) return false;
                int vendor = device.DetectVendor(), original;
                if (!device.Read(vendor, out original) || !KnownMode(vendor, original)) return false;
                int target = PerformanceModeOf((Vendor)vendor);
                if (original == target) return true; // 固件写入前保存原值，用户原本已选中的模式不归本程序。
                if (!SaveSnapshot(vendor + "|" + original + "|" + target)) return false;
                if (!admission() || !device.OnAcPower)
                {
                    SaveSnapshot("");
                    return false;
                }
                int check;
                if (device.Write(vendor, target) && device.Read(vendor, out check) && check == target)
                {
                    Logger.Log(Lang.T("log.laptopperf.applied"));
                    return true;
                }
                Restore();
                return false;
            }
        }

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
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection rows = searcher.Get())
                        foreach (ManagementObject row in rows) { row.Dispose(); return true; }
                }
            }
            catch { }
            return false;
        }

private static ManagementObject First(string className)
        {
            using (var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM " + className))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                using (ManagementObjectCollection rows = searcher.Get())
                    foreach (ManagementObject row in rows) return row;
            }
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
                        using (ManagementBaseObject result = gz.InvokeMethod("GetSmartFanMode", null, InvocationOptions()))
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
                            using (ManagementBaseObject result = atk.InvokeMethod("DSTS", args, InvocationOptions()))
                            {
                                if (result == null) return false;
                                uint status = Convert.ToUInt32(result["device_status"]);
                                // 高位为支持标志，低八位才是模式值。
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
                            using (gz.InvokeMethod("SetSmartFanMode", args, InvocationOptions())) { }
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
                            using (atk.InvokeMethod("DEVS", args, InvocationOptions())) { }
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

        private static InvokeMethodOptions InvocationOptions()
        { return new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) }; }

        public static bool Restore()
        {
            lock (lk)
            {
                string raw;
                if (!Settings.TryLoadStr(SnapKey, out raw) || raw == null) return false;
                if (raw.Length == 0) return true;
                string[] parts = raw.Split('|');
                int vendorRaw, original;
                if ((parts.Length != 2 && parts.Length != 3) || !int.TryParse(parts[0], out vendorRaw)
                    || !int.TryParse(parts[1], out original) || !KnownMode(vendorRaw, original)) return false;
                var vendor = (Vendor)vendorRaw;
                int target = PerformanceModeOf(vendor), now;
                if (parts.Length == 3 && (!int.TryParse(parts[2], out target)
                    || target != PerformanceModeOf(vendor))) return false;
                IBackend device = Backend;
                // 读取失败时禁止固件写入，未完成收据不能丢弃。
                if (!device.Read(vendorRaw, out now) || now < 0) return false;
                if (now != target)
                {
                    // 厂商工具或用户改过模式后不再恢复。
                    if (!SaveSnapshot("")) return false;
                    Logger.Log(Lang.T(now == original ? "log.laptopperf.6" : "log.laptopperf.5"));
                    return true;
                }
                int check;
                if (!device.Write(vendorRaw, original) || !device.Read(vendorRaw, out check) || check != original)
                { Logger.Log(Lang.T("log.laptopperf.4")); return false; }
                if (!SaveSnapshot("")) return false;
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
