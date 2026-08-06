// @author bdth 2074055628@qq.com
// 文件用途 枚举本机显示适配器 按 PCI 厂商与总线位置区分核显与独显 排除虚拟适配器

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum GpuVendor
    {
        Unknown = 0,
        Nvidia = 1,
        Amd = 2,
        Intel = 3
    }

    internal sealed class GpuAdapter
    {
        public string Name;
        public string HardwareId;
        public GpuVendor Vendor;
        public bool Integrated;
        public long VideoMemoryBytes;
        public int BusNumber = -1;

        public string VendorText
        {
            get
            {
                switch (Vendor)
                {
                    case GpuVendor.Nvidia: return "NVIDIA";
                    case GpuVendor.Amd: return "AMD";
                    case GpuVendor.Intel: return "Intel";
                    default: return "未知厂商";
                }
            }
        }

        public string KindText
        {
            get { return Integrated ? "核显" : "独显"; }
        }
    }

    internal static class GpuInventory
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint SpdrpBusNumber = 0x00000015;
        private const string ClassPath =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        private static readonly object lk = new object();
        private static GpuAdapter[] cached;

        public static GpuAdapter[] Adapters()
        {
            lock (lk)
            {
                if (cached == null) cached = Build();
                return cached;
            }
        }

        public static bool HasDiscrete
        {
            get
            {
                foreach (GpuAdapter a in Adapters()) if (!a.Integrated) return true;
                return false;
            }
        }

        public static bool IntegratedOnly
        {
            get
            {
                GpuAdapter[] all = Adapters();
                return all.Length > 0 && !HasDiscrete;
            }
        }

        public static GpuAdapter Primary()
        {
            GpuAdapter best = null;
            foreach (GpuAdapter a in Adapters())
            {
                if (best == null) { best = a; continue; }
                if (best.Integrated != a.Integrated) { if (best.Integrated) best = a; continue; }
                if (a.VideoMemoryBytes > best.VideoMemoryBytes) best = a;
            }
            return best;
        }

        public static bool IsIntegratedDevice(string hardwareId)
        {
            string key = VenDevKey(hardwareId);
            if (key == null) return false;
            foreach (GpuAdapter a in Adapters())
                if (string.Equals(VenDevKey(a.HardwareId), key, StringComparison.OrdinalIgnoreCase))
                    return a.Integrated;
            return false;
        }

        public static string Describe()
        {
            GpuAdapter[] all = Adapters();
            if (all.Length == 0) return null;
            var parts = new List<string>();
            foreach (GpuAdapter a in all) parts.Add(a.Name + "（" + a.KindText + "）");
            return string.Join(" + ", parts.ToArray());
        }

        internal static GpuVendor VendorOf(string hardwareId)
        {
            if (string.IsNullOrEmpty(hardwareId)) return GpuVendor.Unknown;
            if (hardwareId.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0) return GpuVendor.Nvidia;
            if (hardwareId.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0) return GpuVendor.Amd;
            if (hardwareId.IndexOf("VEN_1022", StringComparison.OrdinalIgnoreCase) >= 0) return GpuVendor.Amd;
            if (hardwareId.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0) return GpuVendor.Intel;
            return GpuVendor.Unknown;
        }

        internal static bool IsPciAdapter(string hardwareId)
        {
            return !string.IsNullOrEmpty(hardwareId)
                && hardwareId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IntegratedFrom(GpuVendor vendor, int busNumber, long videoMemoryBytes)
        {
            if (vendor == GpuVendor.Nvidia) return false;
            if (busNumber >= 0) return busNumber == 0;
            return videoMemoryBytes > 0 && videoMemoryBytes < 2147483648L;
        }

        internal static string VenDevKey(string hardwareId)
        {
            if (string.IsNullOrEmpty(hardwareId)) return null;
            int ven = hardwareId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
            int dev = hardwareId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
            if (ven < 0 || dev < 0 || dev + 8 > hardwareId.Length) return null;
            return (hardwareId.Substring(ven, 8) + "&" + hardwareId.Substring(dev, 8)).ToUpperInvariant();
        }

        private static GpuAdapter[] Build()
        {
            var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try { CollectBusNumbers(byKey); }
            catch { }

            var list = new List<GpuAdapter>();
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(ClassPath))
                {
                    if (root == null) return list.ToArray();
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (RegistryKey key = root.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            string hardwareId = key.GetValue("MatchingDeviceId") as string;
                            if (!IsPciAdapter(hardwareId)) continue;
                            string name = Compact(key.GetValue("DriverDesc") as string);
                            if (name.Length == 0) continue;
                            GpuVendor vendor = VendorOf(hardwareId);
                            long memory = VideoMemoryBytes(key);
                            int bus;
                            string venDev = VenDevKey(hardwareId);
                            if (venDev == null || !byKey.TryGetValue(venDev, out bus)) bus = -1;
                            list.Add(new GpuAdapter
                            {
                                Name = name,
                                HardwareId = hardwareId,
                                Vendor = vendor,
                                BusNumber = bus,
                                VideoMemoryBytes = memory,
                                Integrated = IntegratedFrom(vendor, bus, memory)
                            });
                        }
                    }
                }
            }
            catch { }
            return list.ToArray();
        }

        private static long VideoMemoryBytes(RegistryKey key)
        {
            string[] names = { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" };
            foreach (string name in names)
            {
                try
                {
                    object raw = key.GetValue(name);
                    if (raw is long) return (long)raw;
                    if (raw is int) return (uint)(int)raw;
                    var bytes = raw as byte[];
                    if (bytes != null && bytes.Length >= 4)
                    {
                        long value = 0;
                        for (int i = Math.Min(8, bytes.Length) - 1; i >= 0; i--) value = (value << 8) | bytes[i];
                        if (value > 0) return value;
                    }
                }
                catch { }
            }
            return 0;
        }

        private static string Compact(string value)
        {
            string text = (value ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(C)", "")
                .Replace("®", "").Replace("™", "");
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0) text = text.Replace("  ", " ");
            return text.Trim();
        }

        private static void CollectBusNumbers(Dictionary<string, int> byKey)
        {
            IntPtr devInfo = IntPtr.Zero;
            try
            {
                devInfo = SetupDiGetClassDevsW(ref DisplayClass, null, IntPtr.Zero, DigcfPresent);
                if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return;
                var data = new SpDevinfoData();
                data.Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                for (uint index = 0; SetupDiEnumDeviceInfo(devInfo, index, ref data); index++)
                {
                    var id = new StringBuilder(512);
                    uint required;
                    if (!SetupDiGetDeviceInstanceIdW(devInfo, ref data, id, 512, out required)) continue;
                    string venDev = VenDevKey(id.ToString());
                    if (venDev == null) continue;
                    int bus;
                    if (!TryReadBusNumber(devInfo, ref data, out bus)) continue;
                    byKey[venDev] = bus;
                }
            }
            finally
            {
                if (devInfo != IntPtr.Zero && devInfo != new IntPtr(-1))
                    try { SetupDiDestroyDeviceInfoList(devInfo); } catch { }
            }
        }

        private static bool TryReadBusNumber(IntPtr devInfo, ref SpDevinfoData data, out int bus)
        {
            bus = -1;
            var buffer = new byte[4];
            uint dataType, required;
            if (!SetupDiGetDeviceRegistryPropertyW(devInfo, ref data, SpdrpBusNumber,
                    out dataType, buffer, 4, out required))
                return false;
            bus = BitConverter.ToInt32(buffer, 0);
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, uint index, ref SpDevinfoData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devInfo, ref SpDevinfoData data,
            StringBuilder instanceId, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devInfo, ref SpDevinfoData data,
            uint property, out uint dataType, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);
    }
}
