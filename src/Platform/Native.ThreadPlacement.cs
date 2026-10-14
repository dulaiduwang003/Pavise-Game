using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadSelectedCpuSets(IntPtr thread, [Out] uint[] ids, uint count, out uint required);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[] ids, uint count);
        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadGroupAffinity { public UIntPtr Mask; public ushort Group, Reserved0, Reserved1, Reserved2; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadGroupAffinity(IntPtr thread, out ThreadGroupAffinity affinity);

        internal static bool ThreadAllowsMask(IntPtr thread, ulong mask)
        {
            ThreadGroupAffinity a;
            return GetThreadGroupAffinity(thread, out a) && a.Group == 0 && mask != 0
                && (mask & ~a.Mask.ToUInt64()) == 0;
        }
        internal static uint[] QueryThreadCpuSets(IntPtr thread)
        {
            try
            {
                uint count;
                bool ok = GetThreadSelectedCpuSets(thread, null, 0, out count);
                if (count == 0) return ok ? new uint[0] : null;
                if (count > 4096) return null;
                var ids = new uint[count]; uint actual;
                return GetThreadSelectedCpuSets(thread, ids, count, out actual) && actual == count ? ids : null;
            }
            catch { return null; }
        }
        internal static bool WriteThreadCpuSets(IntPtr thread, uint[] ids)
        {
            try { return ids != null && SetThreadSelectedCpuSets(thread, ids.Length == 0 ? null : ids, (uint)ids.Length); }
            catch { return false; }
        }
    }
}
