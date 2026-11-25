using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private const uint DigcfPresent = 2u;

        private const uint SpdrpBusNumber = 21u;

        private const int RestartSettleSeconds = 15;

        private const string ClassPath = "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");

        private static readonly object refreshLock = new object();

        private static readonly object restartLock = new object();

        private static readonly GpuAdapter[] empty = new GpuAdapter[0];

        private static readonly GpuAdapter[] unverifiedEmpty = new GpuAdapter[0];

        private static volatile GpuAdapter[] cached;

        private static int activeRestarts;

        private static int restartGeneration;

        private static long restartSettleUntil;

        private static bool degradeLogged;

        public static bool HasDiscrete
        {
            get
            {
                GpuAdapter[] adaptersResult = Adapters();
                foreach (GpuAdapter gpuAdapter in adaptersResult)
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
                GpuAdapter[] adaptersResult = Adapters();
                foreach (GpuAdapter gpuAdapter in adaptersResult)
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
                foreach (GpuAdapter adapter in adaptersResult)
                {
                    if (!adapter.Integrated) return false;
                }
                return adaptersResult.Length > 0;
            }
        }

        public static GpuAdapter[] Adapters()
        {
            if (cached == null) Refresh(true);
            return CachedAdapters();
        }

        internal static GpuAdapter[] CachedAdapters()
        {
            return CopyAdapters(cached ?? empty);
        }

        internal static bool CachedReadFailed
        {
            get { return ReferenceEquals(cached, unverifiedEmpty); }
        }

        internal static void Refresh()
        {
            Refresh(false);
        }

        private static void Refresh(bool initializeOnly)
        {
            lock (refreshLock)
            {
                if (initializeOnly && cached != null) return;
                int generation;
                lock (restartLock)
                {
                    if (activeRestarts != 0) return;
                    generation = restartGeneration;
                }

                GpuAdapter[] adapters;
                try
                {
#if PAVISE_DELTA_FORCE_GPU_CHECKS
                    adapters = ReadForTest();
#else
                    adapters = Build();
#endif
                }
                catch
                {
                    adapters = UnverifiedInventory();
                }

                lock (restartLock)
                {
                    if (generation != restartGeneration) return;
                    GpuAdapter[] previous = cached ?? empty;
                    if (adapters == null)
                    {
                        if (cached == null) cached = unverifiedEmpty;
                        return;
                    }
                    if (Stopwatch.GetTimestamp() < restartSettleUntil && !IncludesPreviousAdapters(adapters, previous)) return;
                    cached = CopyAdapters(adapters);
                }
            }
        }

        private static GpuAdapter[] CopyAdapters(GpuAdapter[] adapters)
        {
            if (adapters.Length == 0) return empty;
            var copy = new GpuAdapter[adapters.Length];
            for (int i = 0; i < adapters.Length; i++) copy[i] = adapters[i].Copy();
            return copy;
        }

        private static bool IncludesPreviousAdapters(GpuAdapter[] adapters, GpuAdapter[] previous)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuAdapter adapter in adapters) present.Add(adapter.InstanceId);
            foreach (GpuAdapter adapter in previous)
            {
                if (!present.Contains(adapter.InstanceId)) return false;
            }
            return true;
        }

        // 重启期间保留原清单
        internal static void BeginAdapterRestart()
        {
            lock (restartLock)
            {
                activeRestarts++;
                restartGeneration++;
            }
        }

        // 设备重新出现前保留原清单
        internal static void EndAdapterRestart()
        {
            lock (restartLock)
            {
                if (--activeRestarts == 0)
                    restartSettleUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * RestartSettleSeconds;
            }
        }

#if PAVISE_DELTA_FORCE_GPU_CHECKS
        internal static Func<GpuAdapter[]> ReadForTest;

        internal static void ResetForTest()
        {
            lock (refreshLock)
            lock (restartLock)
            {
                cached = null;
                activeRestarts = 0;
                restartGeneration = 0;
                restartSettleUntil = 0;
            }
        }
#endif

        public static GpuAdapter Primary()
        {
            GpuAdapter gpuAdapter = null;
            GpuAdapter[] adaptersResult = Adapters();
            foreach (GpuAdapter gpuAdapter2 in adaptersResult)
            {
                if (gpuAdapter == null)
                {
                    gpuAdapter = gpuAdapter2;
                }
                else if (gpuAdapter.Integrated != gpuAdapter2.Integrated)
                {
                    if (gpuAdapter.Integrated)
                    {
                        gpuAdapter = gpuAdapter2;
                    }
                }
                else if (gpuAdapter2.VideoMemoryBytes > gpuAdapter.VideoMemoryBytes)
                {
                    gpuAdapter = gpuAdapter2;
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
            GpuAdapter[] adaptersResult = Adapters();
            foreach (GpuAdapter gpuAdapter in adaptersResult)
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
            List<string> list = new List<string>();
            GpuAdapter[] gpuAdapterItems = adaptersResult;
            foreach (GpuAdapter gpuAdapter in gpuAdapterItems)
            {
                list.Add(gpuAdapter.Name + " " + gpuAdapter.KindText + " ");
            }
            return string.Join(" + ", list.ToArray());
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
            if (bindings.Length == 0) return empty;
            Dictionary<string, bool> dictionary2 = DxCoreGpuProbe.CollectIntegrated();
            List<GpuAdapter> list = new List<GpuAdapter>();
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(ClassPath))
                {
                    if (registryKey == null)
                    {
                        return UnverifiedInventory();
                    }
                    foreach (GpuDriverBinding binding in bindings)
                    {
                        using (RegistryKey registryKey2 = registryKey.OpenSubKey(binding.SoftwareKey))
                        {
                            if (registryKey2 == null)
                            {
                                return UnverifiedInventory();
                            }
                            string hardwareId = registryKey2.GetValue("MatchingDeviceId") as string;
                            if (!GpuInventoryDiscovery.MatchesInstance(binding.InstanceId, hardwareId))
                            {
                                return UnverifiedInventory();
                            }
                            string driverName = registryKey2.GetValue("DriverDesc") as string;
                            string compactResult = Compact(driverName);
                            if (compactResult.Length == 0)
                            {
                                return UnverifiedInventory();
                            }
                            GpuVendor vendor = VendorOf(hardwareId);
                            long videoMemoryBytes = VideoMemoryBytes(registryKey2);
                            string venDevKeyResult = VenDevKey(hardwareId);
                            int value;
                            if (!buses.TryGetValue(binding.InstanceId, out value))
                            {
                                value = -1;
                            }
                            bool value2;
                            bool? driverReported = ((venDevKeyResult != null && dictionary2.TryGetValue(venDevKeyResult, out value2)) ? new bool?(value2) : null);
                            if (!driverReported.HasValue && !degradeLogged)
                            {
                                degradeLogged = true;
                                Logger.Log(() => Lang.T("log.gpuinventory.6") + compactResult);
                            }
                            list.Add(new GpuAdapter
                            {
                                Name = compactResult,
                                DriverName = driverName.Trim(),
                                HardwareId = hardwareId,
                                InstanceId = binding.InstanceId,
                                Vendor = vendor,
                                BusNumber = value,
                                VideoMemoryBytes = videoMemoryBytes,
                                IntegratedKnown = driverReported.HasValue,
                                Integrated = IntegratedFrom(vendor, value, videoMemoryBytes, driverReported)
                            });
                        }
                    }
                }
            }
            catch
            {
                return UnverifiedInventory();
            }
            return list.ToArray();
        }

        private static string ReadCurrentDriver(string instanceId)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
                return key == null ? null : key.GetValue("Driver") as string;
        }

        private static GpuAdapter[] UnverifiedInventory()
        {
            Logger.Warn(Logger.Text("GPU 清单暂无法核实，保留现有缓存。", "GPU inventory could not be verified. Keeping the cached inventory."));
            return null;
        }

        private static long VideoMemoryBytes(RegistryKey key)
        {
            string[] textEntries = new string[2] { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" };
            string[] textEntries2 = textEntries;
            foreach (string name in textEntries2)
            {
                try
                {
                    object value = key.GetValue(name);
                    if (value is long)
                    {
                        return (long)value;
                    }
                    if (value is int)
                    {
                        return (uint)(int)value;
                    }
                    byte[] valueOffset = value as byte[];
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
            IntPtr intPtr = IntPtr.Zero;
            try
            {
                intPtr = SetupDiGetClassDevsW(ref DisplayClass, null, IntPtr.Zero, 2u);
                if (intPtr == IntPtr.Zero || intPtr == new IntPtr(-1))
                {
                    return false;
                }
                SpDevinfoData data = default(SpDevinfoData);
                data.Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                for (uint iterationIndex = 0u; ; iterationIndex++)
                {
                    if (!SetupDiEnumDeviceInfo(intPtr, iterationIndex, ref data))
                        return Marshal.GetLastWin32Error() == 259;
                    StringBuilder stringBuilder = new StringBuilder(512);
                    uint required;
                    if (!SetupDiGetDeviceInstanceIdW(intPtr, ref data, stringBuilder, 512u, out required))
                    {
                        if (Marshal.GetLastWin32Error() != 122 || required == 0 || required > 4096) return false;
                        stringBuilder = new StringBuilder((int)required);
                        if (!SetupDiGetDeviceInstanceIdW(intPtr, ref data, stringBuilder, required, out required)) return false;
                    }
                    string stringBuilderText = stringBuilder.ToString();
                    if (stringBuilderText.Length == 0) return false;
                    if (IsPciAdapter(stringBuilderText))
                    {
                        instances.Add(stringBuilderText);
                        int bus;
                        if (TryReadBusNumber(intPtr, ref data, out bus))
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
                if (intPtr != IntPtr.Zero && intPtr != new IntPtr(-1))
                {
                    try
                    {
                        SetupDiDestroyDeviceInfoList(intPtr);
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
            if (!SetupDiGetDeviceRegistryPropertyW(devInfo, ref data, SpdrpBusNumber, out dataType, rawBytes, 4u, out required))
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
