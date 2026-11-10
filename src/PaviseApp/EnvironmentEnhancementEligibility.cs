using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class EnvironmentEnhancementEligibility
    {
        internal sealed class MemorySnapshot
        {
            internal ulong Installed;

            internal ulong Total;

            internal ulong Available;
        }

        private struct MemoryStatus
        {
            internal uint Length;

            internal uint Load;

            internal ulong Total;

            internal ulong Available;

            internal ulong TotalPageFile;

            internal ulong AvailablePageFile;

            internal ulong TotalVirtual;

            internal ulong AvailableVirtual;

            internal ulong ExtendedVirtual;
        }

        internal const ulong GiB = 1073741824uL;

        internal static bool Allowed
        {
            get
            {
                try
                {
                    MemorySnapshot memorySnapshot = Sample();
                    return memorySnapshot != null && Fits(memorySnapshot.Installed, memorySnapshot.Total, memorySnapshot.Available);
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static string Description
        {
            get
            {
                return PerformancePolicy.Text("需 32 GB 及以上内存，可用至少 8 GB 且不少于总内存的四分之一。", "Requires at least 32 GB RAM, with at least 8 GB and one quarter of usable memory available.");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kib);

        internal static bool Fits(ulong installed, ulong total, ulong available)
        {
            if (total == 0 || available > total || (installed != 0 && installed < total))
            {
                return false;
            }
            if (((installed != 0) ? (installed >= 34359738368L) : (total >= 33285996544L)) && available >= 8589934592L)
            {
                return available >= total / 4;
            }
            return false;
        }

        private static MemorySnapshot Sample()
        {
            MemoryStatus currentMemoryStatus = default(MemoryStatus);
            currentMemoryStatus.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (!GlobalMemoryStatusEx(ref currentMemoryStatus))
            {
                return null;
            }
            ulong installed = 0uL;
            try
            {
                ulong kib;
                if (GetPhysicallyInstalledSystemMemory(out kib) && kib <= 18014398509481983L)
                {
                    installed = kib * 1024;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            MemorySnapshot memorySnapshot = new MemorySnapshot();
            memorySnapshot.Installed = installed;
            memorySnapshot.Total = currentMemoryStatus.Total;
            memorySnapshot.Available = currentMemoryStatus.Available;
            return memorySnapshot;
        }
    }
}
