// @author bdth 2074055628@qq.com
// File purpose D3DKMT adapter and VRAM interop
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtLuid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtOpenAdapterFromLuid
        {
            public D3dkmtLuid AdapterLuid;
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtCloseAdapter
        {
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtQueryVideoMemoryInfo
        {
            public IntPtr hProcess;
            public uint hAdapter;
            public uint MemorySegmentGroup;
            public ulong Budget;
            public ulong CurrentUsage;
            public ulong CurrentReservation;
            public ulong AvailableForReservation;
            public uint PhysicalAdapterIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtChangeVideoMemoryReservation
        {
            public ulong hProcess;
            public uint hAdapter;
            public uint MemorySegmentGroup;
            public ulong Reservation;
            public uint PhysicalAdapterIndex;
        }

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid p);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter p);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryVideoMemoryInfo(ref D3dkmtQueryVideoMemoryInfo p);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTChangeVideoMemoryReservation(ref D3dkmtChangeVideoMemoryReservation p);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint required);
    }
}
