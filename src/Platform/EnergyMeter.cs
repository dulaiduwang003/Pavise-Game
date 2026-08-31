// @author bdth 2074055628@qq.com
// 文件用途 纯用户态读 CPU 功耗 走 Windows 能量计量接口 EMI 不需要内核驱动
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal enum EnergyRail { Package = 0, Dram = 1, Cores = 2, Uncore = 3 }

    // 微软的 PPM 驱动会把 Intel RAPL 暴露成 EMI 通道 于是不装内核驱动也能读到瓦数
    //   本机实测通道名 dRAPL_Package0_PKG / _DRAM / _PP0 / _PP1
    //   PKG 45.35W PP0 38.64W DRAM 3.23W PP1 1.38W 三个子域相加不超过 PKG 自洽
    // 这条路要 OEM 或平台驱动确实发布了通道 不是每台机器都有 没有就老实报不支持
    //   RAPL 的 MSR 路要 msr.sys 越不过内核边界 PDH 的 Power Meter 面向整机且推导方式未规定
    //   都不能替代 所以探不到 EMI 就没有别的用户态办法 不要用别的数据硬凑瓦数
    internal static class EnergyMeter
    {
        private static readonly Guid DeviceEnergyMeter =
            new Guid("45BD8344-7ED6-49CF-A440-C276C933B053");

        private const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;
        private const uint GenericRead = 0x80000000;
        private const uint ShareReadWrite = 3, OpenExisting = 3;

        // CTL_CODE 参数依次是 FILE_DEVICE_UNKNOWN=0x22 功能号 METHOD_BUFFERED=0 FILE_READ_ACCESS=1
        private const uint IoctlVersion = 0x224000;
        private const uint IoctlMetadataSize = 0x224004;
        private const uint IoctlMetadata = 0x224008;
        private const uint IoctlMeasurement = 0x22400C;

        // EMI_CHANNEL_MEASUREMENT_DATA 是 8 字节累计能量 pWh 加 8 字节绝对时间 100ns
        internal const int ChannelStride = 16;

        // 瓦 = pWh 换 J 乘 3.6e-9 再除以 100ns 换秒的 1e-7 合起来就是 0.036
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
        // 设备上报的全部名字 认不认得出都留着
        //   通道命名是各家自己定的 这套后缀是照 Intel 的 dRAPL_Package0_PKG 写的
        //   AMD 或别家换个叫法就会全部落到 -1 明明能读却被判成读不到
        //   把原始名字留下来并写进日志 拿到真机名字再补进 ClassifyRail 比在这里猜强
        private static string[] rawChannelNames = new string[0];
        private static int[] railIndex = new int[4] { -1, -1, -1, -1 };

        public static bool Available
        {
            get
            {
                lock (sync)
                {
                    EnsureProbedLocked();
                    // 功耗让路验证的是封装瓦数 只有单核通道等于验不了 不算可用
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

        // 认通道名 EMI 本身不规定含义 Intel RAPL 的域名才是判据
        //   PKG 封装总 PP0 核心 PP1 核显 DRAM 内存 名字大小写和前缀各家不同 只看结尾的域
        //   按包含匹配而不是后缀 通道命名各家不同 只认后缀会把换了叫法的平台整个漏掉
        //   顺序必须先具体后笼统 dRAPL_Package0_DRAM 同时含 PACKAGE 和 DRAM 先判 DRAM 才对
        //   宁可漏认也不许错认 认错轨等于读了别的东西的瓦数 后面的判定全建在错数上
        private static bool Has(string n, string token)
        {
            return n.IndexOf(token, StringComparison.Ordinal) >= 0;
        }

        // 认出 dRAPL_Package0_Core7_CORE 这类逐核通道 CORE 后面紧跟数字才算
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
            // CoreN_ 是某一个核的功耗 不是核心总功耗 认成 Cores 会让人拿单核当全部
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

        // 一次采样的原始快照 相邻两次相减才是功率
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

        // 两次快照之间某条轨的平均功率 读不到给 -1
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

        // 必须枚举全部设备 不能只看 index 0
        //   Intel 只发布一个 EMI 设备 恰好第一个就是对的
        //   AMD 8940HX 实测发布 16 个 每个物理核一个 只有其中一个带封装通道
        //     设备 #0 dRAPL_Package0_PKG | dRAPL_Package0_Core0_CORE 封装 17.94W
        //     设备 #2 dRAPL_Package0_Core1_CORE 只有单核 0.13W
        //     其余 14 个同理 各带一个 CoreN
        //   SetupAPI 的返回顺序不保证 只取第一个会拿到只含单核的设备 于是判成读不到
        //   功耗让路要的是封装瓦数 所以挑设备的判据就是有没有封装通道
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
                    if (!unreadable && railIndex[(int)EnergyRail.Package] >= 0) return;   // 就是它
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            // 一个带封装通道的都没有 保留看到过的全部名字 好让日志说清楚是什么情况
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
                // EMI_METADATA_SIZE 是一个 UINT32 不是 UINT64
                //   驱动只写 4 字节 按 8 字节读会把后面的未初始化内存当高位 拿到垃圾长度
                byte[] sizeBuf = Ioctl(h, IoctlMetadataSize, 8);
                if (sizeBuf == null || sizeBuf.Length < 4) { unreadable = true; return; }
                long mdSize = sizeBuf.Length >= 8
                    ? BitConverter.ToInt64(sizeBuf, 0) : BitConverter.ToUInt32(sizeBuf, 0);
                if (mdSize <= 0 || mdSize > 65536) { unreadable = true; return; }
                byte[] md = Ioctl(h, IoctlMetadata, (int)mdSize);
                if (md == null) { unreadable = true; return; }

                // 元数据结构逐版本不同 不硬解析 只扫出 UTF-16 名字再按 RAPL 域名归类
                //   通道顺序就是测量块里的顺序 名字里能认出域的才建立映射
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
