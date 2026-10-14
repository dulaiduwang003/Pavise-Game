// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal enum EnergyRail { Package = 0, Dram = 1, Cores = 2, Uncore = 3 }

    // 功耗只使用驱动提供的 EMI 通道，缺失时返回不支持。
    internal static class EnergyMeter
    {
        private static readonly Guid DeviceEnergyMeter =
            new Guid("45BD8344-7ED6-49CF-A440-C276C933B053");

        private const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;
        private const uint GenericRead = 0x80000000;
        private const uint ShareReadWrite = 3, OpenExisting = 3;

        private const uint IoctlVersion = 0x224000;
        private const uint IoctlMetadataSize = 0x224004;
        private const uint IoctlMetadata = 0x224008;
        private const uint IoctlMeasurement = 0x22400C;

        // 能量单位为 pWh，时间单位为 100 ns。
        internal const int ChannelStride = 16;

        // 两次读数的功率换算系数为 0.036。
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
        private static string[] rawChannelNames = new string[0];
        private static int[] railIndex = new int[4] { -1, -1, -1, -1 };

        public static bool Available
        {
            get
            {
                lock (sync)
                {
                    EnsureProbedLocked();
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

        // 先匹配 DRAM 等具体通道，避免误归为整包功耗。
        private static bool Has(string n, string token)
        {
            return n.IndexOf(token, StringComparison.Ordinal) >= 0;
        }

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
            // 单核功耗不能当作全部核心功耗。
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

        // 枚举全部 EMI 设备，首个设备未必包含整包通道。
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
                    if (!unreadable && railIndex[(int)EnergyRail.Package] >= 0) return;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

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
                // EMI 元数据长度为 UINT32，读取八字节会混入未初始化数据。
                byte[] sizeBuf = Ioctl(h, IoctlMetadataSize, 8);
                if (sizeBuf == null || sizeBuf.Length < 4) { unreadable = true; return; }
                long mdSize = sizeBuf.Length >= 8
                    ? BitConverter.ToInt64(sizeBuf, 0) : BitConverter.ToUInt32(sizeBuf, 0);
                if (mdSize <= 0 || mdSize > 65536) { unreadable = true; return; }
                byte[] md = Ioctl(h, IoctlMetadata, (int)mdSize);
                if (md == null) { unreadable = true; return; }

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
