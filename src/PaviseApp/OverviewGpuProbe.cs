using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 显示信息有意不携带驱动键或控制目标身份。
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

    internal enum OverviewGpuProbeState { Success, NoData, NativeFailure }

    internal sealed class OverviewGpuProbeResult
    {
        internal readonly OverviewGpuInfo Info;
        internal readonly OverviewGpuProbeState State;
        internal readonly string Failure;
        internal OverviewGpuProbeResult(OverviewGpuInfo info, string failure)
        {
            Info = info; Failure = failure;
            State = info != null ? OverviewGpuProbeState.Success
                : string.IsNullOrEmpty(failure) ? OverviewGpuProbeState.NoData : OverviewGpuProbeState.NativeFailure;
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

        // 继承的 IDXGIObject、IDXGIFactory 槽位保持原生顺序。
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
            return ReadResult().Info;
        }

        internal static OverviewGpuProbeResult ReadResult()
        {
            string dxgiFailure, displayFailure;
            OverviewGpuInfo info = ReadDxgi(out dxgiFailure);
            if (info != null) return new OverviewGpuProbeResult(info, null);
            info = ReadDisplayDevices(out displayFailure);
            // 正常无设备与接口失败分开；任一备用渠道得到有效显示信息后即缓存成功。
            return new OverviewGpuProbeResult(info, dxgiFailure ?? displayFailure);
        }

        internal static OverviewGpuInfo SelectDxgi(OverviewGpuInfo selected, string name, ulong memoryBytes, uint flags)
        {
            if ((flags & DxgiRemoteOrSoftware) != 0 || string.IsNullOrWhiteSpace(name)) return selected;
            // 显存相同时按 DXGI 枚举顺序取舍；显存用于候选排序，不能确定 GPU 类型。
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

        private static OverviewGpuInfo ReadDxgi(out string failure)
        {
            failure = null;
            OverviewGpuInfo selected = null;
            IDXGIFactory1 factory = null;
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                int created = CreateDXGIFactory1(ref iid, out factory);
                if (created < 0 || factory == null) { failure = "CreateDXGIFactory1 0x" + created.ToString("X8"); return null; }
                // 执行上限也用于防止异常驱动陷入无限循环。
                for (uint index = 0; index < 256; index++)
                {
                    IDXGIAdapter1 adapter = null;
                    try
                    {
                        int status = factory.EnumAdapters1(index, out adapter);
                        if (status == DxgiNotFound) break;
                        if (status < 0 || adapter == null) { failure = "EnumAdapters1 0x" + status.ToString("X8"); continue; }
                        AdapterDescription description;
                        int described = adapter.GetDesc1(out description);
                        if (described >= 0)
                            selected = SelectDxgi(selected, description.Description,
                                description.DedicatedVideoMemory.ToUInt64(), description.Flags);
                        else failure = "GetDesc1 0x" + described.ToString("X8");
                    }
                    catch (Exception error) { failure = error.GetType().Name; }
                    finally { Release(adapter); }
                }
            }
            catch (Exception error) { failure = error.GetType().Name; }
            finally { Release(factory); }
            return selected;
        }

        private static OverviewGpuInfo ReadDisplayDevices(out string failure)
        {
            failure = null;
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
                    catch (Exception error) { failure = error.GetType().Name; }
                }
            }
            catch (Exception error) { failure = error.GetType().Name; }
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
