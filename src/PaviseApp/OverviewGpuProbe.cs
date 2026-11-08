using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // Display facts deliberately carry no driver key or control-target identity.
    internal sealed class OverviewGpuInfo
    {
        internal readonly string Name;
        internal readonly ulong MemoryBytes;

        internal OverviewGpuInfo(string name, ulong memoryBytes)
        {
            Name = name;
            MemoryBytes = memoryBytes;
        }
    }

    internal static class OverviewGpuProbe
    {
        private const int DxgiNotFound = unchecked((int)0x887A0002);
        private const uint DxgiRemoteOrSoftware = 3;
        private const uint DisplayAttached = 1;
        private const uint DisplayPrimary = 4;
        private const uint DisplayMirroring = 8;
        private const uint DisplayRemote = 0x04000000;

        // Keep the inherited IDXGIObject / IDXGIFactory slots in native order.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr value);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
            [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
            [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr window);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
            [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr value);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
            [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
            [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
            [PreserveSig] int GetDesc(IntPtr description);
            [PreserveSig] int CheckInterfaceSupport(ref Guid iid, out long version);
            [PreserveSig] int GetDesc1(out AdapterDescription description);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string Description;
            internal uint VendorId, DeviceId, SubSysId, Revision;
            internal UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            internal uint LuidLow;
            internal int LuidHigh;
            internal uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            internal uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string DeviceString;
            internal uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string DeviceKey;
        }

        internal static OverviewGpuInfo Read()
        {
            return ReadDxgi() ?? ReadDisplayDevices();
        }

        internal static OverviewGpuInfo SelectDxgi(OverviewGpuInfo selected, string name, ulong memoryBytes, uint flags)
        {
            if ((flags & DxgiRemoteOrSoftware) != 0 || string.IsNullOrWhiteSpace(name)) return selected;
            // Break memory ties in DXGI order; memory ranks candidates but does not identify GPU kind.
            if (selected != null && selected.MemoryBytes >= memoryBytes) return selected;
            return new OverviewGpuInfo(name.Trim(), memoryBytes);
        }

        internal static int DisplayRank(string name, string deviceId, uint flags)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(deviceId)
                || !deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                || (flags & (DisplayMirroring | DisplayRemote)) != 0) return -1;
            return (flags & DisplayPrimary) != 0 ? 2 : ((flags & DisplayAttached) != 0 ? 1 : 0);
        }

        private static OverviewGpuInfo ReadDxgi()
        {
            OverviewGpuInfo selected = null;
            IDXGIFactory1 factory = null;
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == null) return null;
                // The bound also prevents a defective driver from looping forever.
                for (uint index = 0; index < 256; index++)
                {
                    IDXGIAdapter1 adapter = null;
                    try
                    {
                        int status = factory.EnumAdapters1(index, out adapter);
                        if (status == DxgiNotFound) break;
                        if (status < 0 || adapter == null) continue;
                        AdapterDescription description;
                        if (adapter.GetDesc1(out description) >= 0)
                            selected = SelectDxgi(selected, description.Description,
                                description.DedicatedVideoMemory.ToUInt64(), description.Flags);
                    }
                    catch { }
                    finally { Release(adapter); }
                }
            }
            catch { }
            finally { Release(factory); }
            return selected;
        }

        private static OverviewGpuInfo ReadDisplayDevices()
        {
            OverviewGpuInfo selected = null;
            int selectedRank = -1;
            try
            {
                for (uint index = 0; index < 256; index++)
                {
                    try
                    {
                        DisplayDevice device = default(DisplayDevice);
                        device.Size = (uint)Marshal.SizeOf(typeof(DisplayDevice));
                        if (!EnumDisplayDevicesW(null, index, ref device, 0)) break;
                        int rank = DisplayRank(device.DeviceString, device.DeviceId, device.StateFlags);
                        if (rank > selectedRank)
                        {
                            selected = new OverviewGpuInfo(device.DeviceString.Trim(), 0);
                            selectedRank = rank;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return selected;
        }

        private static void Release(object value)
        {
            if (value == null) return;
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice displayDevice, uint flags);
    }
}
