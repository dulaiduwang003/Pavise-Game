// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class IrqDevice
    {
        public string InstanceId = "";
        public string Name = "";
        public string Service = "";
        public string Bus = "";
        public string ClassGuid = "";
        public string DriverVersion = "";
        public int Policy;
        public ulong Mask;
        internal bool ConfigurationKnown;
        public int MessageCount;
        public bool MultiMessageRisk;

        public bool IsPinned { get { return Policy == 4 && Mask != 0; } }
    }

    internal static class IrqDeviceInventory
    {
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
        private const string AffSuffix = @"\Device Parameters\Interrupt Management\Affinity Policy";
        private const string IntrSuffix = @"\Device Parameters\Interrupt Management";
        private const string MsiSuffix = IntrSuffix + @"\MessageSignaledInterruptProperties";
        private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

        private static readonly string[] Buses = { "PCI", "USB", "HDAUDIO", "ACPI" };
        private const int MaxPerBus = 512;

        public static List<IrqDevice> Enumerate()
        {
            var list = new List<IrqDevice>();
            foreach (string bus in Buses)
            {
                try { Walk(bus, list); }
                catch { }
            }
            return list;
        }

        private static void Walk(string bus, List<IrqDevice> list)
        {
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(EnumRoot + bus))
            {
                if (root == null) return;
                int seen = 0;
                foreach (string hw in root.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey hwKey = root.OpenSubKey(hw))
                        {
                            if (hwKey == null) continue;
                            foreach (string inst in hwKey.GetSubKeyNames())
                            {
                                if (++seen > MaxPerBus) return;
                                AddDevice(bus + "\\" + hw + "\\" + inst, bus, list, Read);
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        internal static void AddDevice(string id, string bus, List<IrqDevice> list, Func<string, string, IrqDevice> read)
        {
            try { IrqDevice device = read(id, bus); if (device != null) list.Add(device); }
            catch { }
        }

        private static IrqDevice Read(string instanceId, string bus)
        {
            using (RegistryKey im = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + IntrSuffix))
            {
                if (im == null) return null;
            }
            using (RegistryKey ctl = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + @"\Control"))
            {
                if (ctl == null) return null;
            }
            var d = new IrqDevice();
            d.InstanceId = instanceId;
            d.Bus = bus;
            d.ClassGuid = ReadStr(instanceId, "ClassGUID");
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId))
                {
                    if (k != null)
                    {
                        d.Name = (k.GetValue("FriendlyName") as string)
                            ?? (k.GetValue("DeviceDesc") as string) ?? "";
                        int semi = d.Name.LastIndexOf(';');
                        if (semi >= 0 && semi + 1 < d.Name.Length) d.Name = d.Name.Substring(semi + 1);
                        d.Service = (k.GetValue("Service") as string) ?? "";
                    }
                }
            }
            catch { }
            if (d.Name.Length == 0) d.Name = DriverDeviceResolver.ShortId(instanceId);
            try
            {
                string driverKey = ReadStr(instanceId, "Driver");
                if (driverKey.Length > 0)
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\Class\" + driverKey))
                        if (k != null) d.DriverVersion = k.GetValue("DriverVersion") as string ?? "";
            }
            catch { }
            d.MessageCount = ReadMessageCount(instanceId);
            d.MultiMessageRisk = LooksMultiQueue(d);

            try
            {
                using (RegistryKey a = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + AffSuffix))
                {
                    d.ConfigurationKnown = true;
                    if (a != null)
                    {
                        object p = a.GetValue("DevicePolicy");
                        if (p != null) { if (!(p is int)) d.ConfigurationKnown = false; else d.Policy = (int)p; }
                        object maskValue = a.GetValue("AssignmentSetOverride");
                        var raw = maskValue as byte[];
                        if (maskValue != null && (raw == null || raw.Length != 8)) d.ConfigurationKnown = false;
                        if (raw != null) d.Mask = IrqAffinityEngine.BytesToMask(raw);
                        object pri = a.GetValue("DevicePriority");
                        if (pri != null && !(pri is int)) d.ConfigurationKnown = false;
                    }
                }
            }
            catch { d.ConfigurationKnown = false; }
            return d;
        }

        private static string ReadStr(string instanceId, string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId))
                    return k == null ? "" : ((k.GetValue(name) as string) ?? "");
            }
            catch { return ""; }
        }

        private static int ReadMessageCount(string instanceId)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + MsiSuffix))
                {
                    if (k == null) return 0;
                    object limit = k.GetValue("MessageNumberLimit");
                    if (limit == null) return 0;
                    return Convert.ToInt32(limit);
                }
            }
            catch { return 0; }
        }

        internal static bool LooksMultiQueue(IrqDevice d)
        {
            if (d == null) return false;
            if (d.MessageCount > 1) return true;
            return string.Equals(d.ClassGuid, NetClassGuid, StringComparison.OrdinalIgnoreCase)
                && d.MessageCount == 0;
        }
    }
}
