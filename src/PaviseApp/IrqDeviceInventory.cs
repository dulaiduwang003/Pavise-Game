using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IrqDeviceInventory
    {

        private static readonly string[] Buses = new string[4] { "PCI", "USB", "HDAUDIO", "ACPI" };

        public static List<IrqDevice> Enumerate()
        {
            List<IrqDevice> irqDevices = new List<IrqDevice>();
            foreach (string bus in Buses)
            {
                try
                {
                    Walk(bus, irqDevices);
                }
                catch
                {
                }
            }
            return irqDevices;
        }

        private static void Walk(string bus, List<IrqDevice> list)
        {
            using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + bus))
            {
                if (registryKey == null)
                {
                    return;
                }
                int visitedDevices = 0;
                foreach (string entryKey in registryKey.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(entryKey))
                        {
                            if (currentRegistryKey == null)
                            {
                                continue;
                            }
                            foreach (string entryKey2 in currentRegistryKey.GetSubKeyNames())
                            {
                                if (++visitedDevices > 512)
                                {
                                    return;
                                }
                                AddDevice(bus + "\\" + entryKey + "\\" + entryKey2, bus, list, Read);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static void AddDevice(string id, string bus, List<IrqDevice> list, Func<string, string, IrqDevice> read)
        {
            try
            {
                IrqDevice irqDevice = read(id, bus);
                if (irqDevice != null)
                {
                    list.Add(irqDevice);
                }
            }
            catch
            {
            }
        }

        private static IrqDevice Read(string instanceId, string bus)
        {
            using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management"))
            {
                if (registryKey == null)
                {
                    return null;
                }
            }
            using (RegistryKey currentRegistryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Control"))
            {
                if (currentRegistryKey == null)
                {
                    return null;
                }
            }
            IrqDevice irqDevice = new IrqDevice();
            irqDevice.InstanceId = instanceId;
            irqDevice.Bus = bus;
            irqDevice.ClassGuid = ReadStr(instanceId, "ClassGUID");
            try
            {
                using (RegistryKey candidateRegistryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
                {
                    if (candidateRegistryKey != null)
                    {
                        irqDevice.Name = (candidateRegistryKey.GetValue("FriendlyName") as string) ?? (candidateRegistryKey.GetValue("DeviceDesc") as string) ?? "";
                        int matchIndex = irqDevice.Name.LastIndexOf(';');
                        if (matchIndex >= 0 && matchIndex + 1 < irqDevice.Name.Length)
                        {
                            irqDevice.Name = irqDevice.Name.Substring(matchIndex + 1);
                        }
                        irqDevice.Service = (candidateRegistryKey.GetValue("Service") as string) ?? "";
                    }
                }
            }
            catch
            {
            }
            if (irqDevice.Name.Length == 0)
            {
                irqDevice.Name = DriverDeviceResolver.ShortId(instanceId);
            }
            try
            {
                string str = ReadStr(instanceId, "Driver");
                if (str.Length > 0)
                {
                    using (RegistryKey resolvedRegistryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\" + str))
                    {
                        if (resolvedRegistryKey != null)
                        {
                            irqDevice.DriverVersion = (resolvedRegistryKey.GetValue("DriverVersion") as string) ?? "";
                        }
                    }
                }
            }
            catch
            {
            }
            irqDevice.MessageCount = ReadMessageCount(instanceId);
            irqDevice.MultiMessageRisk = LooksMultiQueue(irqDevice);
            try
            {
                using (RegistryKey selectedRegistryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\Affinity Policy"))
                {
                    irqDevice.ConfigurationKnown = true;
                    if (selectedRegistryKey != null)
                    {
                        object instance = selectedRegistryKey.GetValue("DevicePolicy");
                        if (instance != null)
                        {
                            if (!(instance is int))
                            {
                                irqDevice.ConfigurationKnown = false;
                            }
                            else
                            {
                                irqDevice.Policy = (int)instance;
                            }
                        }
                        object currentInstance = selectedRegistryKey.GetValue("AssignmentSetOverride");
                        byte[] value2Offset = currentInstance as byte[];
                        if (currentInstance != null && (value2Offset == null || value2Offset.Length != 8))
                        {
                            irqDevice.ConfigurationKnown = false;
                        }
                        if (value2Offset != null)
                        {
                            irqDevice.Mask = IrqAffinityEngine.BytesToMask(value2Offset);
                        }
                        object candidateInstance = selectedRegistryKey.GetValue("DevicePriority");
                        if (candidateInstance != null && !(candidateInstance is int))
                        {
                            irqDevice.ConfigurationKnown = false;
                        }
                    }
                }
            }
            catch
            {
                irqDevice.ConfigurationKnown = false;
            }
            return irqDevice;
        }

        private static string ReadStr(string instanceId, string name)
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
                {
                    return (registryKey == null) ? "" : ((registryKey.GetValue(name) as string) ?? "");
                }
            }
            catch
            {
                return "";
            }
        }

        private static int ReadMessageCount(string instanceId)
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties"))
                {
                    if (registryKey == null)
                    {
                        return 0;
                    }
                    object instance = registryKey.GetValue("MessageNumberLimit");
                    if (instance == null)
                    {
                        return 0;
                    }
                    return Convert.ToInt32(instance);
                }
            }
            catch
            {
                return 0;
            }
        }

        internal static bool LooksMultiQueue(IrqDevice d)
        {
            if (d == null)
            {
                return false;
            }
            if (d.MessageCount > 1)
            {
                return true;
            }
            if (string.Equals(d.ClassGuid, "{4d36e972-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
            {
                return d.MessageCount == 0;
            }
            return false;
        }
    }
}
