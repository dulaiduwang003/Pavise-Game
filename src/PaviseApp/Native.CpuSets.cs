using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class Native
    {

        private struct ThreadGroupAffinity
        {
            public UIntPtr Mask;

            public ushort Group;

            public ushort Reserved0;

            public ushort Reserved1;

            public ushort Reserved2;
        }

        public static bool TrySetCpuSets(IntPtr h, uint[] ids)
        {
            if (ids == null || ids.Length == 0)
            {
                return false;
            }
            try
            {
                return SetProcessDefaultCpuSets(h, ids, (uint)ids.Length);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryClearCpuSets(IntPtr h)
        {
            try
            {
                return SetProcessDefaultCpuSets(h, null, 0u);
            }
            catch
            {
                return false;
            }
        }

        public static uint[] QueryCpuSets(IntPtr h)
        {
            try
            {
                uint required;
                bool processDefaultCpuSets = GetProcessDefaultCpuSets(h, null, 0u, out required);
                if (required == 0)
                {
                    return processDefaultCpuSets ? new uint[0] : null;
                }
                uint[] numericValueItems = new uint[required];
                uint required2;
                return (GetProcessDefaultCpuSets(h, numericValueItems, (uint)numericValueItems.Length, out required2) && required2 == required) ? numericValueItems : null;
            }
            catch
            {
                return null;
            }
        }

        public static int QueryThreadOwnerPid(IntPtr thread)
        {
            if (thread == IntPtr.Zero)
            {
                return -1;
            }
            try
            {
                uint processIdOfThread = GetProcessIdOfThread(thread);
                return (processIdOfThread != 0 && processIdOfThread <= int.MaxValue) ? ((int)processIdOfThread) : (-1);
            }
            catch
            {
                return -1;
            }
        }

        public static bool RestoreCpuSets(IntPtr h, uint[] ids)
        {
            if (ids != null)
            {
                if (ids.Length != 0)
                {
                    return TrySetCpuSets(h, ids);
                }
                return TryClearCpuSets(h);
            }
            return false;
        }

        public static bool RestoreCpuSetsVerified(IntPtr h, uint[] ids)
        {
            if (RestoreCpuSets(h, ids))
            {
                return CpuSetsMatch(h, ids);
            }
            return false;
        }

        public static bool CpuSetsMatch(IntPtr h, uint[] expected)
        {
            if (expected == null)
            {
                return false;
            }
            uint[] cpuSets = QueryCpuSets(h);
            if (cpuSets == null || cpuSets.Length != expected.Length)
            {
                return false;
            }
            HashSet<uint> hashSet = new HashSet<uint>(cpuSets);
            foreach (uint item in expected)
            {
                if (!hashSet.Contains(item))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool TryClearCpuSets(int pid)
        {
            IntPtr intPtr = OpenProcess(8192, false, pid);
            if (intPtr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return TryClearCpuSets(intPtr);
            }
            finally
            {
                CloseHandle(intPtr);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint required);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadSelectedCpuSets(IntPtr thread, [Out] uint[] ids, uint count, out uint required);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[] ids, uint count);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadGroupAffinity(IntPtr thread, out ThreadGroupAffinity affinity);

        internal static bool ThreadAllowsMask(IntPtr thread, ulong mask)
        {
            ThreadGroupAffinity affinity;
            if (GetThreadGroupAffinity(thread, out affinity) && affinity.Group == 0 && mask != 0)
            {
                return (mask & ~affinity.Mask.ToUInt64()) == 0;
            }
            return false;
        }

        internal static uint[] QueryThreadCpuSets(IntPtr thread)
        {
            try
            {
                uint required;
                bool threadSelectedCpuSets = GetThreadSelectedCpuSets(thread, null, 0u, out required);
                if (required == 0)
                {
                    return threadSelectedCpuSets ? new uint[0] : null;
                }
                if (required > 4096)
                {
                    return null;
                }
                uint[] numericValueItems = new uint[required];
                uint required2;
                return (GetThreadSelectedCpuSets(thread, numericValueItems, required, out required2) && required2 == required) ? numericValueItems : null;
            }
            catch
            {
                return null;
            }
        }

        internal static bool WriteThreadCpuSets(IntPtr thread, uint[] ids)
        {
            try
            {
                return ids != null && SetThreadSelectedCpuSets(thread, (ids.Length == 0) ? null : ids, (uint)ids.Length);
            }
            catch
            {
                return false;
            }
        }

    }
}
