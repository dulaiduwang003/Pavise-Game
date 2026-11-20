using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class Native
    {

#pragma warning disable 0649 // 这些互操作字段由原生接口填充。
        public struct D3dkmtLuid
        {
            public uint LowPart;

            public int HighPart;
        }

        public struct D3dkmtOpenAdapterFromLuid
        {
            public D3dkmtLuid AdapterLuid;

            public uint hAdapter;
        }

        public struct D3dkmtCloseAdapter
        {
            public uint hAdapter;
        }

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
#pragma warning restore 0649

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid p);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter p);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryVideoMemoryInfo(ref D3dkmtQueryVideoMemoryInfo p);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int cls);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr h, int cls);

    }
}
