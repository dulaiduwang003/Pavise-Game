// 文件用途 Intel IGCL 的 ABI 对应 drivers.gpu.control-library 提交 b6c462933502e13d1537dd5024949a51be30e63d
// 只用系统里已装的 Intel 运行时 不打包任何驱动二进制或头文件
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal sealed class IntelGraphicsAdapter
    {
        public string Id, Name;
        public uint VendorId;
        public bool Integrated;
        public bool LowLatencySupported;
    }

    internal enum IntelGraphicsWriteResult { NotIssued, Written, Conflict, Cancelled, Uncertain }

    internal interface IIntelGraphicsControl
    {
        bool TryGetAdapters(out IntelGraphicsAdapter[] adapters);
        bool TryReadLowLatency(string adapterId, out uint value);
        IntelGraphicsWriteResult TryWriteLowLatency(string adapterId, uint expected, uint value,
            Func<bool> mayContinue);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IntelCtlInit
    {
        public uint Size;
        public byte Version;
        public uint AppVersion, Flags, SupportedVersion;
        public Guid ApplicationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IntelCtlAdapterProperties
    {
        public uint Size;
        public byte Version;
        public IntPtr DeviceId;
        public uint DeviceIdSize, DeviceType, SupportedFunctions;
        public ulong DriverVersion, FirmwareMajor, FirmwareMinor, FirmwareBuild;
        public uint VendorId, PciDeviceId, Revision, Eus, SubSlices, Slices;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100)] public byte[] Name;
        public uint AdapterFlags, Frequency;
        public ushort SubsystemId, SubsystemVendorId;
        public byte Bus, Device, Function;
        public uint XeCores;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 108)] public byte[] Reserved;
    }

    // 能力联合体里有一个 20 字节的 float/int 成员 按 8 字节对齐
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct IntelCtlPropertyInfo
    {
        [FieldOffset(0)] public ulong SupportedTypes;
        [FieldOffset(8)] public uint DefaultType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IntelCtlFeatureDetails
    {
        public uint FeatureType, ValueType;
        public IntelCtlPropertyInfo Value;
        public int CustomValueSize;
        public IntPtr CustomValue;
        [MarshalAs(UnmanagedType.I1)] public bool PerAppSupport;
        public long ConflictingFeatures;
        public short MiscSupport, Reserved, Reserved1, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IntelCtlFeatureCaps
    {
        public uint Size;
        public byte Version;
        public uint Count;
        public IntPtr Details;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct IntelCtlPropertyValue
    {
        [FieldOffset(0)] public uint EnumValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IntelCtlFeatureValue
    {
        public uint Size;
        public byte Version;
        public uint FeatureType;
        public IntPtr ApplicationName;
        public sbyte ApplicationNameLength;
        [MarshalAs(UnmanagedType.I1)] public bool Set;
        public uint ValueType;
        public IntelCtlPropertyValue Value;
        public int CustomValueSize;
        public IntPtr CustomValue;
    }

    internal sealed class IntelGraphicsApi : IIntelGraphicsControl
    {
        internal const uint LowLatencyFeature = 16;
        internal const uint EnumValueType = 4;
        private const uint SearchSystem32 = 0x00000800;
        private readonly object gate = new object();
        private bool attempted;
        private IntPtr module, api;
        private FnClose close;
        private FnEnumerate enumerate;
        private FnProperties properties;
        private FnCaps capabilities;
        private FnFeature feature;
        private List<NativeAdapter> cachedDevices;
        private int cachedTick;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint FnInit(ref IntelCtlInit args, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint FnClose(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint FnEnumerate(IntPtr handle, ref uint count, IntPtr devices);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint FnProperties(IntPtr device, ref IntelCtlAdapterProperties value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint FnCaps(IntPtr device, ref IntelCtlFeatureCaps value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint FnFeature(IntPtr device, ref IntelCtlFeatureValue value);

        public bool TryGetAdapters(out IntelGraphicsAdapter[] adapters)
        {
            adapters = null;
            lock (gate)
            {
                try
                {
                    RefuseNativeInTests();
                    List<NativeAdapter> devices = cachedDevices;
                    if (devices == null || unchecked((uint)(Environment.TickCount - cachedTick)) >= 5000)
                        if (!Enumerate(out devices)) return false;
                    var result = new List<IntelGraphicsAdapter>();
                    foreach (NativeAdapter device in devices)
                    {
                        IntelGraphicsAdapter source = device.Description;
                        result.Add(new IntelGraphicsAdapter { Id = source.Id, Name = source.Name,
                            VendorId = source.VendorId, Integrated = source.Integrated,
                            LowLatencySupported = source.LowLatencySupported });
                    }
                    adapters = result.ToArray();
                    return true;
                }
                catch { return false; }
            }
        }

        public bool TryReadLowLatency(string adapterId, out uint value)
        {
            value = 0;
            lock (gate)
            {
                try
                {
                    RefuseNativeInTests();
                    NativeAdapter adapter = Find(adapterId, false);
                    return adapter != null && Read(adapter.Handle, out value);
                }
                catch { return false; }
            }
        }

        public IntelGraphicsWriteResult TryWriteLowLatency(string adapterId, uint expected, uint value,
            Func<bool> mayContinue)
        {
            // 唯一允许的改动是 Off 改成基本 On 以及把我们写的基本 On 改回 Off
            // Boost 逐应用继承 补帧和其它设置一概够不着
            if (!((expected == 0 && value == 1) || (expected == 1 && value == 0)))
                return IntelGraphicsWriteResult.NotIssued;
            lock (gate)
            {
                bool issued = false;
                try
                {
                    RefuseNativeInTests();
                    if (!Continue(mayContinue)) return IntelGraphicsWriteResult.Cancelled;
                    NativeAdapter adapter = Find(adapterId, true);
                    if (!Continue(mayContinue)) return IntelGraphicsWriteResult.Cancelled;
                    if (adapter == null || !adapter.Description.LowLatencySupported)
                        return IntelGraphicsWriteResult.NotIssued;
                    uint current;
                    if (!Read(adapter.Handle, out current)) return IntelGraphicsWriteResult.NotIssued;
                    if (current != expected) return IntelGraphicsWriteResult.Conflict;
                    if (!Continue(mayContinue)) return IntelGraphicsWriteResult.Cancelled;
                    IntelCtlFeatureValue request = LowLatencyRequest(true, value);
                    issued = true;
                    return feature(adapter.Handle, ref request) == 0
                        ? IntelGraphicsWriteResult.Written : IntelGraphicsWriteResult.Uncertain;
                }
                catch { return issued ? IntelGraphicsWriteResult.Uncertain : IntelGraphicsWriteResult.NotIssued; }
            }
        }

        internal static IntelCtlFeatureValue LowLatencyRequest(bool set, uint value)
        {
            return new IntelCtlFeatureValue
            {
                Size = (uint)Marshal.SizeOf(typeof(IntelCtlFeatureValue)),
                FeatureType = LowLatencyFeature,
                ValueType = EnumValueType,
                Set = set,
                Value = new IntelCtlPropertyValue { EnumValue = value }
                // 版本 0 应用名为空 长度 0 文档里写明这是全局范围
            };
        }

        internal static bool SafeLowLatencyCapability(IntelCtlFeatureDetails detail)
        {
            const int dx9OrDx11 = 1 | 2, liveChange = 16;
            return detail.FeatureType == LowLatencyFeature && detail.ValueType == EnumValueType
                && (detail.Value.SupportedTypes & 3UL) == 3UL
                && detail.ConflictingFeatures == 0
                && (detail.MiscSupport & dx9OrDx11) != 0 && (detail.MiscSupport & liveChange) != 0;
        }

        internal static bool Continue(Func<bool> callback)
        {
            try { return callback == null || callback(); }
            catch { return false; }
        }

        private sealed class NativeAdapter
        {
            internal IntPtr Handle;
            internal IntelGraphicsAdapter Description;
        }

        private NativeAdapter Find(string id, bool refreshCapabilities)
        {
            if (string.IsNullOrEmpty(id)) return null;
            List<NativeAdapter> all = cachedDevices;
            if (refreshCapabilities || all == null)
                if (!Enumerate(out all)) return null;
            NativeAdapter match = null;
            foreach (NativeAdapter device in all)
            {
                if (device.Description.VendorId != 0x8086 || device.Description.Id != id) continue;
                if (match != null) return null;
                match = device;
            }
            if (match == null && !refreshCapabilities) return Find(id, true);
            if (match != null && !refreshCapabilities)
            {
                // 活动会话的回读不会枚举出每一项 3D 能力
                // 读之前仍然要核实这个句柄的硬件和驱动身份
                IntelCtlAdapterProperties info;
                if (!ReadProperties(match.Handle, out info) || AdapterIdentity(info) != id)
                { cachedDevices = null; return null; }
            }
            return match;
        }

        private bool Read(IntPtr device, out uint value)
        {
            IntelCtlFeatureValue request = LowLatencyRequest(false, 0);
            value = 0;
            if (feature(device, ref request) != 0 || request.ValueType != EnumValueType
                || request.FeatureType != LowLatencyFeature || request.Value.EnumValue > 2) return false;
            value = request.Value.EnumValue;
            return true;
        }

        private bool Enumerate(out List<NativeAdapter> result)
        {
            result = null;
            cachedDevices = null;
            if (!Initialize()) return false;
            uint count = 0;
            if (enumerate(api, ref count, IntPtr.Zero) != 0 || count > 32) return false;
            result = new List<NativeAdapter>();
            if (count == 0) { cachedDevices = result; cachedTick = Environment.TickCount; return true; }
            IntPtr handles = AllocateZeroed(checked((int)count * IntPtr.Size));
            try
            {
                uint capacity = count;
                if (enumerate(api, ref count, handles) != 0 || count > capacity) return false;
                for (int i = 0; i < count; i++)
                {
                    IntPtr handle = Marshal.ReadIntPtr(handles, i * IntPtr.Size);
                    if (handle == IntPtr.Zero) return false;
                    IntelCtlAdapterProperties info;
                    if (!ReadProperties(handle, out info)) return false;
                    if (info.DeviceType != 1) continue;
                    bool supported = false;
                    if (info.VendorId == 0x8086 && (info.SupportedFunctions & 2U) != 0)
                    {
                        if (!GetLowLatencyCapability(handle, out supported)) return false;
                    }
                    string id = info.VendorId == 0x8086 ? AdapterIdentity(info)
                        : "other:" + info.VendorId.ToString("X", CultureInfo.InvariantCulture) + ":" + i;
                    if (id == null) return false;
                    result.Add(new NativeAdapter
                    {
                        Handle = handle,
                        Description = new IntelGraphicsAdapter
                        {
                            Id = id, Name = Encoding.ASCII.GetString(info.Name).TrimEnd('\0'),
                            VendorId = info.VendorId, Integrated = (info.AdapterFlags & 1U) != 0,
                            LowLatencySupported = supported
                        }
                    });
                }
                cachedDevices = result;
                cachedTick = Environment.TickCount;
                return true;
            }
            finally { Marshal.FreeHGlobal(handles); }
        }

        private bool ReadProperties(IntPtr handle, out IntelCtlAdapterProperties info)
        {
            info = new IntelCtlAdapterProperties();
            IntPtr luid = AllocateZeroed(8);
            try
            {
                info = new IntelCtlAdapterProperties
                {
                    Size = (uint)Marshal.SizeOf(typeof(IntelCtlAdapterProperties)), Version = 2,
                    DeviceId = luid, DeviceIdSize = 8, Name = new byte[100], Reserved = new byte[108]
                };
                return properties(handle, ref info) == 0 && info.Size == (uint)Marshal.SizeOf(typeof(IntelCtlAdapterProperties))
                    && info.Name != null && info.Name.Length == 100;
            }
            finally { Marshal.FreeHGlobal(luid); }
        }

        internal static string AdapterIdentity(IntelCtlAdapterProperties info)
        {
            if (info.Version < 2 || info.VendorId == 0 || info.VendorId > 0xFFFF
                || info.PciDeviceId == 0 || info.PciDeviceId > 0xFFFF
                || info.Device > 31 || info.Function > 7 || info.DriverVersion == 0) return null;
            // LUID 重启就变 PCI 位置加子系统再加驱动版本才是跨崩溃稳定的
            // 能防止对着另一块驱动重放
            return string.Format(CultureInfo.InvariantCulture,
                "{0:X4}:{1:X4}:{2:X4}:{3:X4}:{4:X2}:{5:X2}:{6:X2}:{7:X16}",
                info.VendorId, info.PciDeviceId, info.SubsystemVendorId, info.SubsystemId,
                info.Bus, info.Device, info.Function, info.DriverVersion);
        }

        private bool GetLowLatencyCapability(IntPtr device, out bool supported)
        {
            supported = false;
            var caps = new IntelCtlFeatureCaps { Size = (uint)Marshal.SizeOf(typeof(IntelCtlFeatureCaps)) };
            if (capabilities(device, ref caps) != 0 || caps.Count > 128) return false;
            if (caps.Count == 0) return true;
            uint capacity = caps.Count;
            int stride = Marshal.SizeOf(typeof(IntelCtlFeatureDetails));
            IntPtr buffer = AllocateZeroed(checked((int)capacity * stride));
            caps.Details = buffer;
            try
            {
                if (capabilities(device, ref caps) != 0 || caps.Count > capacity || caps.Details != buffer)
                    return false;
                bool found = false;
                for (int i = 0; i < caps.Count; i++)
                {
                    var detail = (IntelCtlFeatureDetails)Marshal.PtrToStructure(
                        IntPtr.Add(buffer, i * stride), typeof(IntelCtlFeatureDetails));
                    if (detail.FeatureType != LowLatencyFeature) continue;
                    if (found) return false;
                    found = true;
                    supported = SafeLowLatencyCapability(detail);
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private bool Initialize()
        {
            RefuseNativeInTests();
            if (attempted) return api != IntPtr.Zero;
            attempted = true;
            module = LoadLibraryExW(IntPtr.Size == 8 ? "ControlLib.dll" : "ControlLib32.dll",
                IntPtr.Zero, SearchSystem32);
            if (module == IntPtr.Zero) return false;
            try
            {
                var init = Function<FnInit>("ctlInit");
                close = Function<FnClose>("ctlClose");
                enumerate = Function<FnEnumerate>("ctlEnumerateDevices");
                properties = Function<FnProperties>("ctlGetDeviceProperties");
                capabilities = Function<FnCaps>("ctlGetSupported3DCapabilities");
                feature = Function<FnFeature>("ctlGetSet3DFeature");
                var args = new IntelCtlInit
                { Size = (uint)Marshal.SizeOf(typeof(IntelCtlInit)), AppVersion = 0x00010001 };
                IntPtr handle;
                if (init(ref args, out handle) != 0 || handle == IntPtr.Zero) return false;
                api = handle;
                AppDomain.CurrentDomain.ProcessExit += CloseAtExit;
                return true;
            }
            finally
            {
                if (api == IntPtr.Zero) { FreeLibrary(module); module = IntPtr.Zero; }
            }
        }

        private T Function<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new MissingMethodException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private void CloseAtExit(object sender, EventArgs args)
        {
            lock (gate)
            {
                try
                {
                    RefuseNativeInTests();
                    if (api != IntPtr.Zero && close(api) == 0)
                    {
                        api = IntPtr.Zero;
                        FreeLibrary(module); module = IntPtr.Zero;
                    }
                }
                catch { }
            }
        }

        private static IntPtr AllocateZeroed(int bytes)
        {
            byte[] zero = new byte[bytes];
            IntPtr memory = Marshal.AllocHGlobal(bytes);
            try { Marshal.Copy(zero, 0, memory, bytes); return memory; }
            catch { Marshal.FreeHGlobal(memory); throw; }
        }

        private static void RefuseNativeInTests()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            throw new InvalidOperationException("Intel graphics native access requires an injected test double.");
#endif
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr library, string name);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr library);
    }
}
