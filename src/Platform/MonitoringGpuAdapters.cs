using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class MonitoringGpuAdapter
    {
        internal long Luid;
        internal string Name;
    }

    // 显卡身份按 LUID 匹配，不能用索引或 PCI 编号代替。
    internal static class MonitoringGpuAdapters
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Description
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
            public uint Vendor, Device, Subsystem, Revision;
            public UIntPtr VideoMemory, SystemMemory, SharedMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }
        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescription(IntPtr adapter, out Description description);
        [StructLayout(LayoutKind.Sequential)]
        private struct AdapterInfoQuery
        {
            public uint Adapter, Type;
            public IntPtr Data;
            public uint Size;
        }
        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int D3DKMTQueryAdapterInfo(ref AdapterInfoQuery query);

        // 虚拟显示设备可能复用物理显卡名称。
        internal static bool IsHardwareRenderer(uint? type)
        {
            // 查询失败时保留候选，避免误判为单显卡。
            return !type.HasValue || ((type.Value & 1) != 0 && (type.Value & 0x44) == 0);
        }

        private static uint? ReadAdapterType(uint low, int high)
        {
            var open = new Native.D3dkmtOpenAdapterFromLuid {
                AdapterLuid = new Native.D3dkmtLuid { LowPart = low, HighPart = high } };
            IntPtr data = IntPtr.Zero;
            try
            {
                if (Native.D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.hAdapter == 0) return null;
                data = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(data, 0);
                var query = new AdapterInfoQuery { Adapter = open.hAdapter, Type = 15, Data = data, Size = 4 };
                if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;
                return unchecked((uint)Marshal.ReadInt32(data));
            }
            catch { return null; }
            finally
            {
                if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
                if (open.hAdapter != 0)
                {
                    var close = new Native.D3dkmtCloseAdapter { hAdapter = open.hAdapter };
                    try { Native.D3DKMTCloseAdapter(ref close); } catch { }
                }
            }
        }

        internal static MonitoringGpuAdapter[] Enumerate()
        {
            IntPtr factory = IntPtr.Zero;
            var result = new List<MonitoringGpuAdapter>();
            try
            {
                var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref id, out factory) < 0 || factory == IntPtr.Zero) return result.ToArray();
                // EnumAdapters1 位于虚表第 12 槽。
                var enumerate = (EnumAdapters)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size), typeof(EnumAdapters));
                for (uint i = 0; i < 32; i++)
                {
                    IntPtr adapter = IntPtr.Zero;
                    try
                    {
                        int status = enumerate(factory, i, out adapter);
                        if (status == unchecked((int)0x887A0002)) return result.ToArray();
                        if (status < 0 || adapter == IntPtr.Zero) return new MonitoringGpuAdapter[0];
                        // GetDesc1 位于虚表第 10 槽。
                        var describe = (GetDescription)Marshal.GetDelegateForFunctionPointer(
                            Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size), typeof(GetDescription));
                        Description description;
                        if (describe(adapter, out description) < 0) return new MonitoringGpuAdapter[0];
                        if ((description.Flags & 3) != 0 || String.IsNullOrWhiteSpace(description.Name)) continue;
                        if (!IsHardwareRenderer(ReadAdapterType(description.LuidLow, description.LuidHigh))) continue;
                        result.Add(new MonitoringGpuAdapter { Name = description.Name.Trim(),
                            Luid = ((long)description.LuidHigh << 32) | description.LuidLow });
                    }
                    finally { if (adapter != IntPtr.Zero) Marshal.Release(adapter); }
                }
            }
            catch { }
            finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
            // 枚举不完整时不能确认唯一显卡。
            return new MonitoringGpuAdapter[0];
        }
    }
}
