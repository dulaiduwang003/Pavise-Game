using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GpuInventory
    {
        private struct SpDevinfoData
        {
            public uint Size;

            public Guid ClassGuid;

            public uint DevInst;

            public IntPtr Reserved;
        }

        private static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");

        private static readonly object lk = new object();

        private static GpuAdapter[] cached;

        private static bool degradeLogged;

        public static bool HasDiscrete
        {
            get
            {
                foreach (GpuAdapter gpuAdapter in Adapters())
                {
                    if (!gpuAdapter.Integrated)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public static bool Hybrid
        {
            get
            {
                bool hasIntegratedGpu = false;
                bool hasDiscreteGpu = false;
                foreach (GpuAdapter gpuAdapter in Adapters())
                {
                    if (gpuAdapter.Integrated)
                    {
                        hasIntegratedGpu = true;
                    }
                    else
                    {
                        hasDiscreteGpu = true;
                    }
                }
                if (hasIntegratedGpu)
                {
                    return hasDiscreteGpu;
                }
                return false;
            }
        }

        public static bool IntegratedOnly
        {
            get
            {
                GpuAdapter[] adaptersResult = Adapters();
                if (adaptersResult.Length > 0)
                {
                    return !HasDiscrete;
                }
                return false;
            }
        }

        public static GpuAdapter[] Adapters()
        {
            lock (lk)
            {
                if (cached == null)
                {
                    cached = Build();
                }
                return cached;
            }
        }

        public static GpuAdapter Primary()
        {
            GpuAdapter gpuAdapter = null;
            foreach (GpuAdapter currentAdaptersResult in Adapters())
            {
                if (gpuAdapter == null)
                {
                    gpuAdapter = currentAdaptersResult;
                }
                else if (gpuAdapter.Integrated != currentAdaptersResult.Integrated)
                {
                    if (gpuAdapter.Integrated)
                    {
                        gpuAdapter = currentAdaptersResult;
                    }
                }
                else if (currentAdaptersResult.VideoMemoryBytes > gpuAdapter.VideoMemoryBytes)
                {
                    gpuAdapter = currentAdaptersResult;
                }
            }
            return gpuAdapter;
        }

        public static bool IsIntegratedDevice(string hardwareId)
        {
            string venDevKeyResult = VenDevKey(hardwareId);
            if (venDevKeyResult == null)
            {
                return false;
            }
            foreach (GpuAdapter gpuAdapter in Adapters())
            {
                if (string.Equals(VenDevKey(gpuAdapter.HardwareId), venDevKeyResult, StringComparison.OrdinalIgnoreCase))
                {
                    return gpuAdapter.Integrated;
                }
            }
            return false;
        }

        public static string Describe()
        {
            GpuAdapter[] adaptersResult = Adapters();
            if (adaptersResult.Length == 0)
            {
                return null;
            }
            List<string> texts = new List<string>();
            foreach (GpuAdapter gpuAdapter in adaptersResult)
            {
                texts.Add(gpuAdapter.Name + " " + gpuAdapter.KindText + " ");
            }
            return string.Join(" + ", texts.ToArray());
        }

        internal static GpuVendor VendorOf(string hardwareId)
        {
            if (string.IsNullOrEmpty(hardwareId))
            {
                return GpuVendor.Unknown;
            }
            if (hardwareId.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GpuVendor.Nvidia;
            }
            if (hardwareId.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GpuVendor.Amd;
            }
            if (hardwareId.IndexOf("VEN_1022", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GpuVendor.Amd;
            }
            if (hardwareId.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GpuVendor.Intel;
            }
            return GpuVendor.Unknown;
        }

        internal static bool IsPciAdapter(string hardwareId)
        {
            if (!string.IsNullOrEmpty(hardwareId))
            {
                return hardwareId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        internal static bool IntegratedFrom(GpuVendor vendor, int busNumber, long videoMemoryBytes, bool? driverReported)
        {
            if (driverReported.HasValue)
            {
                return driverReported.Value;
            }
            if (vendor == GpuVendor.Nvidia)
            {
                return false;
            }
            if (busNumber >= 0)
            {
                return busNumber == 0;
            }
            if (videoMemoryBytes > 0)
            {
                return videoMemoryBytes < 2147483648u;
            }
            return false;
        }

        internal static string VenDevKey(string hardwareId)
        {
            if (string.IsNullOrEmpty(hardwareId))
            {
                return null;
            }
            int matchIndex = hardwareId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
            int matchIndex2 = hardwareId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0 || matchIndex2 < 0 || matchIndex2 + 8 > hardwareId.Length)
            {
                return null;
            }
            return (hardwareId.Substring(matchIndex, 8) + "&" + hardwareId.Substring(matchIndex2, 8)).ToUpperInvariant();
        }

        private static GpuAdapter[] Build()
        {
            List<string> instances;
            Dictionary<string, int> buses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!TryReadPresentInstances(buses, out instances)) return UnverifiedInventory();
            GpuDriverBinding[] bindings;
            if (!GpuInventoryDiscovery.TryMapPresent(instances, ReadCurrentDriver, out bindings))
                return UnverifiedInventory();
            if (bindings.Length == 0) return new GpuAdapter[0];
            Dictionary<string, bool> conditionsByName = DxCoreGpuProbe.CollectIntegrated();
            List<GpuAdapter> gpuAdapters = new List<GpuAdapter>();
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (registryKey == null)
                    {
                        return gpuAdapters.ToArray();
                    }
                    foreach (GpuDriverBinding binding in bindings)
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(binding.SoftwareKey))
                        {
                            if (currentRegistryKey == null)
                            {
                                return UnverifiedInventory();
                            }
                            string hardwareId = currentRegistryKey.GetValue("MatchingDeviceId") as string;
                            if (!GpuInventoryDiscovery.MatchesInstance(binding.InstanceId, hardwareId))
                            {
                                return UnverifiedInventory();
                            }
                            string compactResult = Compact(currentRegistryKey.GetValue("DriverDesc") as string);
                            if (compactResult.Length == 0)
                            {
                                return UnverifiedInventory();
                            }
                            GpuVendor vendor = VendorOf(hardwareId);
                            long videoMemoryBytes = VideoMemoryBytes(currentRegistryKey);
                            string venDevKeyResult = VenDevKey(hardwareId);
                            int buse;
                            if (!buses.TryGetValue(binding.InstanceId, out buse))
                            {
                                buse = -1;
                            }
                            bool integratedDriverReported;
                            bool? driverReported = ((venDevKeyResult != null && conditionsByName.TryGetValue(venDevKeyResult, out integratedDriverReported)) ? new bool?(integratedDriverReported) : null);
                            if (!driverReported.HasValue && !degradeLogged)
                            {
                                degradeLogged = true;
                                Logger.Log(Lang.T("log.gpuinventory.6", 0) + compactResult);
                            }
                            gpuAdapters.Add(new GpuAdapter
                            {
                                Name = compactResult,
                                HardwareId = hardwareId,
                                Vendor = vendor,
                                BusNumber = buse,
                                VideoMemoryBytes = videoMemoryBytes,
                                IntegratedKnown = driverReported.HasValue,
                                Integrated = IntegratedFrom(vendor, buse, videoMemoryBytes, driverReported)
                            });
                        }
                    }
                }
            }
            catch
            {
                return UnverifiedInventory();
            }
            return gpuAdapters.ToArray();
        }

        private static string ReadCurrentDriver(string instanceId)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
                return key == null ? null : key.GetValue("Driver") as string;
        }

        private static GpuAdapter[] UnverifiedInventory()
        {
            Logger.Warn("GPU 清单：当前 PCI 适配器映射不完整，暂不能选择控制目标。");
            return new GpuAdapter[0];
        }

        private static long VideoMemoryBytes(RegistryKey key)
        {
            string[] memoryValueNames = new string[2] { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" };
            foreach (string name in memoryValueNames)
            {
                try
                {
                    object instance = key.GetValue(name);
                    if (instance is long)
                    {
                        return (long)instance;
                    }
                    if (instance is int)
                    {
                        return (uint)(int)instance;
                    }
                    byte[] valueOffset = instance as byte[];
                    if (valueOffset != null && valueOffset.Length >= 4)
                    {
                        long videoMemoryBytesResult = 0L;
                        for (int valueOffsetIndex = Math.Min(8, valueOffset.Length) - 1; valueOffsetIndex >= 0; valueOffsetIndex--)
                        {
                            videoMemoryBytesResult = (videoMemoryBytesResult << 8) | valueOffset[valueOffsetIndex];
                        }
                        if (videoMemoryBytesResult > 0)
                        {
                            return videoMemoryBytesResult;
                        }
                    }
                }
                catch
                {
                }
            }
            return 0L;
        }

        private static string Compact(string value)
        {
            string replaceResult = (value ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(C)", "")
                .Replace("®", "")
                .Replace("™", "");
            while (replaceResult.IndexOf("  ", StringComparison.Ordinal) >= 0)
            {
                replaceResult = replaceResult.Replace("  ", " ");
            }
            return replaceResult.Trim();
        }

        private static bool TryReadPresentInstances(Dictionary<string, int> buses, out List<string> instances)
        {
            instances = new List<string>();
            IntPtr deviceInfoSet = IntPtr.Zero;
            try
            {
                deviceInfoSet = SetupDiGetClassDevsW(ref DisplayClass, null, IntPtr.Zero, 2u);
                if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
                {
                    return false;
                }
                SpDevinfoData data = default(SpDevinfoData);
                data.Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                for (uint iterationIndex = 0u; ; iterationIndex++)
                {
                    if (!SetupDiEnumDeviceInfo(deviceInfoSet, iterationIndex, ref data))
                        return Marshal.GetLastWin32Error() == 259;
                    StringBuilder stringBuilder = new StringBuilder(512);
                    uint required;
                    if (!SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref data, stringBuilder, 512u, out required))
                    {
                        if (Marshal.GetLastWin32Error() != 122 || required == 0 || required > 4096) return false;
                        stringBuilder = new StringBuilder((int)required);
                        if (!SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref data, stringBuilder, required, out required)) return false;
                    }
                    string stringBuilderText = stringBuilder.ToString();
                    if (stringBuilderText.Length == 0) return false;
                    if (IsPciAdapter(stringBuilderText))
                    {
                        instances.Add(stringBuilderText);
                        int bus;
                        if (TryReadBusNumber(deviceInfoSet, ref data, out bus))
                        {
                            buses[stringBuilderText] = bus;
                        }
                    }
                    data.Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                }
            }
            catch { return false; }
            finally
            {
                if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != new IntPtr(-1))
                {
                    try
                    {
                        SetupDiDestroyDeviceInfoList(deviceInfoSet);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool TryReadBusNumber(IntPtr devInfo, ref SpDevinfoData data, out int bus)
        {
            bus = -1;
            byte[] rawBytes = new byte[4];
            uint dataType;
            uint required;
            if (!SetupDiGetDeviceRegistryPropertyW(devInfo, ref data, 21u, out dataType, rawBytes, 4u, out required))
            {
                return false;
            }
            bus = BitConverter.ToInt32(rawBytes, 0);
            return true;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, uint index, ref SpDevinfoData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devInfo, ref SpDevinfoData data, StringBuilder instanceId, uint size, out uint required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devInfo, ref SpDevinfoData data, uint property, out uint dataType, byte[] buffer, uint size, out uint required);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);
    }
}
