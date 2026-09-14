// @author bdth 2074055628@qq.com
// File purpose Pure user-mode CPU power reading via the Windows Energy Metering Interface (EMI), no kernel driver needed
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal enum EnergyRail { Package = 0, Dram = 1, Cores = 2, Uncore = 3 }

    // Microsoft's PPM driver exposes Intel RAPL as EMI channels, so wattage is readable without installing a kernel driver
    //   channel names measured on this machine dRAPL_Package0_PKG / _DRAM / _PP0 / _PP1
    //   PKG 45.35W PP0 38.64W DRAM 3.23W PP1 1.38W, the three sub-domains sum to no more than PKG, self-consistent
    // This path needs the OEM or platform driver to actually publish channels, not every machine has them, report unsupported honestly when absent
    //   the RAPL MSR path needs msr.sys and cannot cross the kernel boundary, PDH's Power Meter is machine-wide with an unspecified derivation
    //   neither can substitute, so without EMI there is no other user-mode way, do not fake wattage from other data
    internal static class EnergyMeter
    {
        private static readonly Guid DeviceEnergyMeter =
            new Guid("45BD8344-7ED6-49CF-A440-C276C933B053");

        private const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;
        private const uint GenericRead = 0x80000000;
        private const uint ShareReadWrite = 3, OpenExisting = 3;

        // CTL_CODE arguments in order: FILE_DEVICE_UNKNOWN=0x22, function number, METHOD_BUFFERED=0, FILE_READ_ACCESS=1
        private const uint IoctlVersion = 0x224000;
        private const uint IoctlMetadataSize = 0x224004;
        private const uint IoctlMetadata = 0x224008;
        private const uint IoctlMeasurement = 0x22400C;

        // EMI_CHANNEL_MEASUREMENT_DATA is 8 bytes of accumulated energy in pWh plus 8 bytes of absolute time in 100ns
        internal const int ChannelStride = 16;

        // Watts = pWh to J times 3.6e-9, divided by 100ns to seconds 1e-7, combined that is 0.036
        internal const double PicoWattHoursPerHundredNsToWatts = 0.036;

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator,
            IntPtr window, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo,
            ref Guid guid, uint index, ref DeviceInterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set,
            ref DeviceInterfaceData data, IntPtr detail, int detailSize, out int required,
            IntPtr devInfoData);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string path, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuf,
            int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static readonly object sync = new object();
        private static bool probed;
        private static string devicePath;
        private static bool unreadable;
        private static string[] channelNames = new string[0];
        // All names reported by the device, kept whether recognized or not
        //   channel naming is vendor-specific, this suffix set was written after Intel's dRAPL_Package0_PKG
        //   AMD or another vendor with different naming would all land on -1, readable yet judged unreadable
        //   keep the raw names and log them, adding real-machine names to ClassifyRail later beats guessing here
        private static string[] rawChannelNames = new string[0];
        private static int[] railIndex = new int[4] { -1, -1, -1, -1 };

        public static bool Available
        {
            get
            {
                lock (sync)
                {
                    EnsureProbedLocked();
                    // Power yield verifies package wattage, having only per-core channels means it cannot verify, not counted as usable
                    return devicePath != null && !unreadable
                        && railIndex[(int)EnergyRail.Package] >= 0;
                }
            }
        }

        public static string Describe()
        {
            lock (sync)
            {
                EnsureProbedLocked();
                return DescribeLocked();
            }
        }

        internal static bool HasRail(EnergyRail rail)
        {
            lock (sync) { EnsureProbedLocked(); return railIndex[(int)rail] >= 0; }
        }

        // Recognize channel names, EMI itself defines no semantics, Intel RAPL domain names are the criteria
        //   PKG package total, PP0 cores, PP1 iGPU, DRAM memory, case and prefix differ per vendor, only the trailing domain matters
        //   match by containment rather than suffix, channel naming differs per vendor, suffix-only would miss a whole platform with different naming
        //   order must go specific before general, dRAPL_Package0_DRAM contains both PACKAGE and DRAM, DRAM must be tested first
        //   better to miss than to misidentify, the wrong rail means reading something else's wattage and every later decision builds on bad numbers
        private static bool Has(string n, string token)
        {
            return n.IndexOf(token, StringComparison.Ordinal) >= 0;
        }

        // Recognize per-core channels like dRAPL_Package0_Core7_CORE, only counts when CORE is immediately followed by a digit
        internal static bool IsPerCore(string upper)
        {
            int at = upper.IndexOf("CORE", StringComparison.Ordinal);
            while (at >= 0)
            {
                int d = at + 4;
                if (d < upper.Length && upper[d] >= '0' && upper[d] <= '9') return true;
                at = upper.IndexOf("CORE", at + 1, StringComparison.Ordinal);
            }
            return false;
        }

        internal static int ClassifyRail(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            string n = name.ToUpperInvariant();
            if (Has(n, "DRAM") || Has(n, "MEMORY")) return (int)EnergyRail.Dram;
            // CoreN_ is the power of one core, not total core power, treating it as Cores would pass a single core off as the whole
            if (IsPerCore(n)) return -1;
            if (Has(n, "PP0") || Has(n, "CORE")) return (int)EnergyRail.Cores;
            if (Has(n, "PP1") || Has(n, "_GT") || Has(n, "IGPU")) return (int)EnergyRail.Uncore;
            if (Has(n, "PKG") || Has(n, "PACKAGE") || n == "CPU"
                || n.StartsWith("CPU_") || Has(n, "SOC")) return (int)EnergyRail.Package;
            return -1;
        }

        internal static double WattsFrom(ulong energyBefore, ulong energyAfter,
            ulong timeBefore, ulong timeAfter)
        {
            if (energyAfter <= energyBefore || timeAfter <= timeBefore) return -1;
            return PicoWattHoursPerHundredNsToWatts
                * (energyAfter - energyBefore) / (double)(timeAfter - timeBefore);
        }

        // Raw snapshot of one sample, power is the difference between two adjacent ones
        public sealed class Sample
        {
            internal readonly byte[] Raw;
            internal Sample(byte[] raw) { Raw = raw; }
            public int Channels { get { return Raw == null ? 0 : Raw.Length / ChannelStride; } }
        }

        public static Sample Take()
        {
            lock (sync)
            {
                EnsureProbedLocked();
                if (devicePath == null || unreadable) return null;
                IntPtr h = CreateFileW(devicePath, GenericRead, ShareReadWrite, IntPtr.Zero,
                    OpenExisting, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) return null;
                try
                {
                    byte[] raw = Ioctl(h, IoctlMeasurement, 4096);
                    return raw == null || raw.Length < ChannelStride ? null : new Sample(raw);
                }
                finally { CloseHandle(h); }
            }
        }

        // Average power of one rail between two snapshots, -1 when unreadable
        public static double Watts(Sample before, Sample after, EnergyRail rail)
        {
            if (before == null || after == null) return -1;
            int idx;
            lock (sync) { EnsureProbedLocked(); idx = railIndex[(int)rail]; }
            if (idx < 0) return -1;
            int off = idx * ChannelStride;
            if (off + ChannelStride > before.Raw.Length || off + ChannelStride > after.Raw.Length)
                return -1;
            return WattsFrom(BitConverter.ToUInt64(before.Raw, off),
                BitConverter.ToUInt64(after.Raw, off),
                BitConverter.ToUInt64(before.Raw, off + 8),
                BitConverter.ToUInt64(after.Raw, off + 8));
        }

        private static void EnsureProbedLocked()
        {
            if (probed) return;
            probed = true;
            try { ProbeLocked(); }
            catch { unreadable = true; channelNames = new string[0]; }
            try { Logger.Log(Lang.T("log.energymeter.1") + DescribeLocked()); }
            catch { }
        }

        private static string DescribeLocked()
        {
            if (devicePath == null) return Lang.T("t.energymeter.1");
            if (unreadable) return Lang.T("t.energymeter.3");
            if (channelNames.Length == 0)
                return Lang.T("t.energymeter.2")
                    + (rawChannelNames.Length > 0
                        ? " [" + string.Join(", ", rawChannelNames) + "]" : "");
            return string.Join(", ", channelNames);
        }

        // All devices must be enumerated, not just index 0
        //   Intel publishes a single EMI device, the first one happens to be right
        //   AMD 8940HX measured publishing 16, one per physical core, only one of them carries the package channel
        //     device #0 dRAPL_Package0_PKG | dRAPL_Package0_Core0_CORE package 17.94W
        //     device #2 dRAPL_Package0_Core1_CORE single core only 0.13W
        //     the other 14 likewise, each carrying one CoreN
        //   SetupAPI return order is not guaranteed, taking the first would pick a single-core-only device and judge it unreadable
        //   power yield wants package wattage, so the device criterion is whether it has a package channel
        private static void ProbeLocked()
        {
            Guid guid = DeviceEnergyMeter;
            IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
            var seen = new List<string>();
            try
            {
                for (uint i = 0; i < 256; i++)
                {
                    var data = new DeviceInterfaceData();
                    data.Size = Marshal.SizeOf(typeof(DeviceInterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    int need;
                    SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(need);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        int dummy;
                        if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, need, out dummy, IntPtr.Zero))
                            path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    if (path == null) continue;

                    devicePath = path;
                    unreadable = false;
                    ReadChannelsLocked();
                    foreach (string n in rawChannelNames) if (!seen.Contains(n)) seen.Add(n);
                    if (!unreadable && railIndex[(int)EnergyRail.Package] >= 0) return;   // this is the one
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            // None carries a package channel, keep every name seen so the log can state clearly what happened
            devicePath = null;
            channelNames = new string[0];
            rawChannelNames = seen.ToArray();
            for (int i = 0; i < railIndex.Length; i++) railIndex[i] = -1;
        }

        private static void ReadChannelsLocked()
        {
            IntPtr h = CreateFileW(devicePath, GenericRead, ShareReadWrite, IntPtr.Zero,
                OpenExisting, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) { unreadable = true; return; }
            try
            {
                byte[] ver = Ioctl(h, IoctlVersion, 2);
                if (ver == null || ver.Length < 2) { unreadable = true; return; }
                // EMI_METADATA_SIZE is a UINT32, not a UINT64
                //   the driver writes only 4 bytes, reading 8 takes the uninitialized memory after it as the high half and yields a garbage length
                byte[] sizeBuf = Ioctl(h, IoctlMetadataSize, 8);
                if (sizeBuf == null || sizeBuf.Length < 4) { unreadable = true; return; }
                long mdSize = sizeBuf.Length >= 8
                    ? BitConverter.ToInt64(sizeBuf, 0) : BitConverter.ToUInt32(sizeBuf, 0);
                if (mdSize <= 0 || mdSize > 65536) { unreadable = true; return; }
                byte[] md = Ioctl(h, IoctlMetadata, (int)mdSize);
                if (md == null) { unreadable = true; return; }

                // Metadata layout differs per version, no hard parsing, just scan out UTF-16 names and classify by RAPL domain name
                //   channel order is the order in the measurement block, mappings are built only for names whose domain is recognized
                var all = new List<string>();
                var names = new List<string>();
                foreach (string s in Utf16Strings(md, 3))
                {
                    all.Add(s);
                    if (ClassifyRail(s) >= 0) names.Add(s);
                }
                rawChannelNames = all.ToArray();
                channelNames = names.ToArray();
                for (int i = 0; i < railIndex.Length; i++) railIndex[i] = -1;
                for (int i = 0; i < channelNames.Length; i++)
                {
                    int rail = ClassifyRail(channelNames[i]);
                    if (rail >= 0 && railIndex[rail] < 0) railIndex[rail] = i;
                }
            }
            finally { CloseHandle(h); }
        }

        private static byte[] Ioctl(IntPtr handle, uint code, int outSize)
        {
            if (outSize <= 0 || outSize > 65536) return null;
            IntPtr buf = Marshal.AllocHGlobal(outSize);
            try
            {
                for (int i = 0; i < outSize; i++) Marshal.WriteByte(buf, i, 0);
                int ret;
                if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, buf, outSize, out ret, IntPtr.Zero))
                    return null;
                if (ret <= 0 || ret > outSize) ret = outSize;
                byte[] b = new byte[ret];
                Marshal.Copy(buf, b, 0, ret);
                return b;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        internal static List<string> Utf16Strings(byte[] blob, int minLength)
        {
            var found = new List<string>();
            if (blob == null) return found;
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < blob.Length; i += 2)
            {
                char c = (char)(blob[i] | (blob[i + 1] << 8));
                if (c >= 32 && c < 127) sb.Append(c);
                else
                {
                    if (sb.Length >= minLength) found.Add(sb.ToString());
                    sb.Length = 0;
                }
            }
            if (sb.Length >= minLength) found.Add(sb.ToString());
            return found;
        }
    }
}
