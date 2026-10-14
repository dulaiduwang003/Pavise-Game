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

    internal sealed class IntelGraphicsApi
    {
        // Endurance Gaming 使用自定义控制块，控制与模式字段不可互换。
        internal const uint EnduranceFeature = 1;
        internal const uint CustomValueType = 5;
        private const int EnduranceBlockBytes = 8;
        private const uint SearchSystem32 = 0x00000800;
        private static readonly IntelGraphicsApi availabilityApi = new IntelGraphicsApi();
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Func<bool> AvailabilityForTest;
#endif
        private readonly object gate = new object();
        private bool attempted;
        private IntPtr module, api;
        private FnClose close;
        private FnEnumerate enumerate;
        private FnProperties properties;
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
        private delegate uint FnFeature(IntPtr device, ref IntelCtlFeatureValue value);

        internal static bool HasAvailable
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (AvailabilityForTest != null)
                {
                    try { return AvailabilityForTest(); }
                    catch { return false; }
                }
#endif
                IntelGraphicsAdapter[] adapters;
                if (!availabilityApi.TryGetAdapters(out adapters) || adapters == null) return false;
                foreach (IntelGraphicsAdapter adapter in adapters)
                    if (adapter != null && adapter.VendorId == 0x8086) return true;
                return false;
            }
        }

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
                            VendorId = source.VendorId, Integrated = source.Integrated });
                    }
                    adapters = result.ToArray();
                    return true;
                }
                catch { return false; }
            }
        }

        public bool TryReadEndurance(out int control, out int mode)
        {
            control = -1; mode = -1;
            lock (gate)
            {
                try
                {
                    RefuseNativeInTests();
                    List<NativeAdapter> devices = cachedDevices;
                    if (devices == null || unchecked((uint)(Environment.TickCount - cachedTick)) >= 5000)
                        if (!Enumerate(out devices)) return false;
                    foreach (NativeAdapter device in devices)
                    {
                        if (device.Description.VendorId != 0x8086) continue;
                        int c, m;
                        if (EnduranceCall(device.Handle, false, 0, 0, out c, out m)) { control = c; mode = m; return true; }
                    }
                    return false;
                }
                catch { return false; }
            }
        }

        public bool TryWriteEndurance(int control, int mode)
        {
            lock (gate)
            {
                try
                {
                    RefuseNativeInTests();
                    List<NativeAdapter> devices;
                    if (!Enumerate(out devices)) return false;
                    bool any = false;
                    foreach (NativeAdapter device in devices)
                    {
                        if (device.Description.VendorId != 0x8086) continue;
                        int c, m;
                        if (EnduranceCall(device.Handle, true, control, mode, out c, out m)) any = true;
                    }
                    return any;
                }
                catch { return false; }
            }
        }

        private bool EnduranceCall(IntPtr device, bool set, int control, int mode, out int outControl, out int outMode)
        {
            outControl = -1; outMode = -1;
            IntPtr block = AllocateZeroed(EnduranceBlockBytes);
            try
            {
                if (set)
                {
                    Marshal.WriteInt32(block, 0, control);
                    Marshal.WriteInt32(block, 4, mode);
                }
                IntelCtlFeatureValue request = EnduranceRequest(set, block);
                if (feature(device, ref request) != 0) return false;
                outControl = Marshal.ReadInt32(block, 0);
                outMode = Marshal.ReadInt32(block, 4);
                return outControl >= 0 && outControl <= 2;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(block); }
        }

        internal static IntelCtlFeatureValue EnduranceRequest(bool set, IntPtr block)
        {
            return new IntelCtlFeatureValue
            {
                Size = (uint)Marshal.SizeOf(typeof(IntelCtlFeatureValue)),
                FeatureType = EnduranceFeature,
                ValueType = CustomValueType,
                Set = set,
                CustomValueSize = EnduranceBlockBytes,
                CustomValue = block
            };
        }

        private sealed class NativeAdapter
        {
            internal IntPtr Handle;
            internal IntelGraphicsAdapter Description;
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
                    string id = info.VendorId == 0x8086 ? AdapterIdentity(info)
                        : "other:" + info.VendorId.ToString("X", CultureInfo.InvariantCulture) + ":" + i;
                    if (id == null) return false;
                    result.Add(new NativeAdapter
                    {
                        Handle = handle,
                        Description = new IntelGraphicsAdapter
                        {
                            Id = id, Name = Encoding.ASCII.GetString(info.Name).TrimEnd('\0'),
                            VendorId = info.VendorId, Integrated = (info.AdapterFlags & 1U) != 0
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
            return string.Format(CultureInfo.InvariantCulture,
                "{0:X4}:{1:X4}:{2:X4}:{3:X4}:{4:X2}:{5:X2}:{6:X2}:{7:X16}",
                info.VendorId, info.PciDeviceId, info.SubsystemVendorId, info.SubsystemId,
                info.Bus, info.Device, info.Function, info.DriverVersion);
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
