using System;
using System.Management;
using System.Threading;

namespace PaviseApp
{
    internal enum LaboratoryEppDeviceKind { Checking, Unknown, Laptop, Desktop }
    internal sealed class LaboratoryEppAvailability
    {
        internal readonly bool Available;
        internal readonly string Chinese, English;
        internal LaboratoryEppAvailability(bool available, string chinese, string english)
        { Available = available; Chinese = chinese; English = english; }
        internal string Detail { get { return PerformancePolicy.Text(Chinese, English); } }
    }

    // Discover chassis once off the UI thread; a battery/UPS or EPP support does not prove a laptop.
    internal static class LaboratoryEppEligibility
    {
        private static int started;
        private static int deviceKind = (int)LaboratoryEppDeviceKind.Checking;
#if PAVISE_SELFTEST
        internal static Func<LaboratoryEppAvailability> ReadForTest;
#endif
        private static LaboratoryEppDeviceKind Device
        {
            get
            {
                if (Interlocked.CompareExchange(ref started, 1, 0) == 0)
                {
                    try { if (!ThreadPool.QueueUserWorkItem(delegate { ProbeDevice(); })) Volatile.Write(ref deviceKind, (int)LaboratoryEppDeviceKind.Unknown); }
                    catch { Volatile.Write(ref deviceKind, (int)LaboratoryEppDeviceKind.Unknown); }
                }
                return (LaboratoryEppDeviceKind)Volatile.Read(ref deviceKind);
            }
        }
        internal static bool VerifiedLaptop
        { get { return Device == LaboratoryEppDeviceKind.Laptop; } }
        internal static LaboratoryEppDeviceKind DeviceKind
        { get { return Device; } }
        internal static LaboratoryEppAvailability Current
        {
            get
            {
#if PAVISE_SELFTEST
                if (ReadForTest != null) return ReadForTest();
#endif
                return Evaluate(Device, Native.HasSystemBattery(), Native.ConfirmedAcPower());
            }
        }
        internal static LaboratoryEppAvailability Evaluate(LaboratoryEppDeviceKind device, bool battery, bool onAc)
        {
            if (device == LaboratoryEppDeviceKind.Checking)
                return new LaboratoryEppAvailability(false, "正在核验设备类型，本策略仅适用笔记本", "Checking device type; this policy is laptop-only");
            if (device == LaboratoryEppDeviceKind.Desktop)
                return new LaboratoryEppAvailability(false, "本机为台式设备，不适用笔记本功耗让路策略", "Desktop device; laptop power yield does not apply");
            if (device != LaboratoryEppDeviceKind.Laptop)
                return new LaboratoryEppAvailability(false, "无法核验笔记本设备类型，本策略不可用", "Laptop device type could not be verified; policy unavailable");
            if (!battery)
                return new LaboratoryEppAvailability(false, "未检测到可核验的笔记本电池，本策略不可用", "A laptop battery could not be verified; policy unavailable");
            if (!onAc)
                return new LaboratoryEppAvailability(false, "需要可核验的交流供电，电池供电时不执行", "Confirmed AC power is required; unavailable on battery");
            return new LaboratoryEppAvailability(true, "", "");
        }
        internal static LaboratoryEppDeviceKind Classify(int[] chassisTypes)
        {
            if (chassisTypes == null || chassisTypes.Length == 0 || chassisTypes.Length > 32) return LaboratoryEppDeviceKind.Unknown;
            bool portable = false, desktop = false;
            foreach (int type in chassisTypes)
            {
                if (type == 8 || type == 9 || type == 10 || type == 11 || type == 14 || type == 30 || type == 31 || type == 32) portable = true;
                else if ((type >= 3 && type <= 7) || type == 13 || (type >= 15 && type <= 29) || type == 33 || type == 34 || type == 35 || type == 36) desktop = true;
                // Other, Unknown and Docking Station do not establish a device type.
            }
            if (portable && !desktop) return LaboratoryEppDeviceKind.Laptop;
            if (desktop && !portable) return LaboratoryEppDeviceKind.Desktop;
            return LaboratoryEppDeviceKind.Unknown;
        }
        private static void ProbeDevice()
        {
            LaboratoryEppDeviceKind result = LaboratoryEppDeviceKind.Unknown;
            try
            {
                var values = new System.Collections.Generic.List<int>();
                var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false };
                using (var query = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"),
                    new ObjectQuery("SELECT ChassisTypes FROM Win32_SystemEnclosure"), options))
                using (ManagementObjectCollection rows = query.Get())
                    foreach (ManagementObject row in rows)
                    {
                        using (row)
                        {
                            Array types = row["ChassisTypes"] as Array;
                            if (types == null || types.Length > 32) continue;
                            foreach (object type in types) values.Add(Convert.ToInt32(type));
                        }
                    }
                result = Classify(values.ToArray());
            }
            catch { result = LaboratoryEppDeviceKind.Unknown; }
            Volatile.Write(ref deviceKind, (int)result);
        }
    }
}
